using Desk.Application.Admin;
using Desk.Application.Connectors;
using Desk.Connectors.Mock;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Mapping health: what a connection's PSA actually sends, set against the rules there are.
///
/// A value no rule maps passes through as the PSA wrote it, which looks exactly like a mapping that
/// let it through on purpose. A status called "Complete" then counted as open work, and the only
/// record that anything was unmapped was a line in a log. These hold the health check to what the
/// tickets themselves say, and the "apply" to changing only what was never mapped.
/// </summary>
public class MappingHealthTests
{
    private static readonly Guid Org = Guid.NewGuid();

    /// <summary>The mock PSA lists the statuses New, In Progress, Waiting Customer, Resolved and Closed, and the priorities Low to Critical.</summary>
    private sealed class Psa(TestClock clock) : IConnectorResolver
    {
        public MockConnector Mock { get; } = new(new MockConnectorOptions(), clock);
        public bool Unreachable { get; set; }

        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default)
            => Unreachable
                ? throw new ConnectorException(ConnectorFailureKind.Timeout, "The PSA did not answer.")
                : Task.FromResult<IServiceManagementConnector>(Mock);
    }

    private sealed record World(AdminHarness H, ConnectionAdminService Service, Psa Psa, Guid Connection, Guid Other);

    private static async Task<World> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        var psa = new Psa(h.Clock);
        PsaConnection Of(string name) => new()
        {
            MspOrganizationId = Org, Name = name, Provider = ProviderType.ConnectWisePsa, ApiEndpoint = "https://cw.example/",
            CredentialSecretRef = "mem://" + name, Status = ConnectionStatus.Healthy, IsEnabled = true,
        };
        var main = Of("Main");
        var other = Of("Other account");
        h.Db.PsaConnections.AddRange(main, other);
        await h.Db.SaveChangesAsync();
        var service = new ConnectionAdminService(h.Db, h.Secrets, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), psa,
            new ConnectionFieldCache(), new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);
        return new World(h, service, psa, main.Id, other.Id);
    }

    private static int _seq;

    private static Ticket T(Guid connection, string psaStatus, string psaPriority, string? portalStatus = null, string? portalPriority = null,
        string? tech = null, string? techName = null) => new()
    {
        MspOrganizationId = Org, PsaConnectionId = connection, Provider = ProviderType.ConnectWisePsa, Origin = TicketOrigin.Psa,
        ExternalTicketId = (++_seq).ToString(), RequesterName = "r", RequesterEmail = "r@a.test", Title = $"Ticket {_seq}",
        PsaStatus = psaStatus, PortalStatus = portalStatus ?? psaStatus, PsaPriority = psaPriority, PortalPriority = portalPriority ?? psaPriority,
        AssignedTechnicianExternalId = tech, AssignedTechnicianName = techName,
    };

    private static FieldMapping Rule(Guid connection, string field, string external, string portal) => new()
    {
        MspOrganizationId = Org, Provider = ProviderType.ConnectWisePsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = connection,
        PortalField = field, ExternalField = field, ExternalValue = external, PortalValue = portal, Direction = MappingDirection.Bidirectional,
    };

    private static MappingValueDto Value(MappingHealthDto health, string field, string value)
        => health.Fields.Single(f => f.Field == field).Items.Single(i => i.Value == value);

    [Fact]
    public async Task Health_is_what_the_tickets_hold_set_against_the_rules_there_are()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.FieldMappings.AddRange(
            Rule(w.Connection, "status", "New", "NEW"), Rule(w.Connection, "status", "Closed", "CLOSED"),
            Rule(w.Connection, "priority", "High", "HIGH"),
            // Another PSA account's rule for the very value this one is missing. It is not this one's.
            Rule(w.Other, "status", "Complete", "CLOSED"));
        w.H.Db.Tickets.AddRange(
            T(w.Connection, "New", "High", "NEW", "HIGH"), T(w.Connection, "New", "High", "NEW", "HIGH"), T(w.Connection, "New", "Medium", "NEW"),
            T(w.Connection, "Complete", "High", portalPriority: "HIGH"), T(w.Connection, "Complete", "Medium"),
            T(w.Connection, "Waiting Customer", "High", portalPriority: "HIGH"),
            T(w.Connection, "Resolved", "High", portalPriority: "HIGH"),
            T(w.Other, "Complete", "Medium"));
        await w.H.Db.SaveChangesAsync();

        var health = await w.Service.MappingHealthAsync(w.Connection);

        var statuses = health.Fields.Single(f => f.Field == "status");
        (statuses.Values, statuses.Mapped, statuses.UnmappedTickets).Should().Be((6, 2, 4),
            "five the PSA lists and one - Complete - it no longer lists but tickets still hold");
        statuses.MappedPct.Should().Be(33.3);
        Value(health, "status", "New").Should().Be(new MappingValueDto("New", "NEW", false, 3, 0, true, null));
        Value(health, "status", "Complete").Should().Be(new MappingValueDto("Complete", null, false, 2, 2, false, "Open"),
            "nothing told the portal that Complete is a way of saying closed, so it counts as open work");
        Value(health, "status", "Resolved").TreatedAs.Should().Be("Finished", "the PSA's own word says so, and the portal reads the word");
        Value(health, "status", "In Progress").Should().Be(new MappingValueDto("In Progress", null, false, 0, 0, true, null),
            "listed by the PSA, on no ticket: unmapped, and nothing is wrong because of it yet");
        statuses.Items.Take(3).Select(i => i.Value).Should().Equal(["Complete", "Resolved", "Waiting Customer"], "what tickets hold unmapped comes first, most tickets first");

        var priorities = health.Fields.Single(f => f.Field == "priority");
        (priorities.Values, priorities.Mapped, priorities.UnmappedTickets).Should().Be((4, 1, 2));
        Value(health, "priority", "Medium").TreatedAs.Should().BeNull("a priority is not open or finished");

        (health.Tickets, health.UnmappedTickets).Should().Be((7, 5), "a ticket with an unmapped status AND an unmapped priority is one ticket");
        health.Level.Should().Be("Warning");
    }

    [Fact]
    public async Task With_no_status_mapped_finished_cannot_be_told_from_open_and_that_blocks()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.Tickets.Add(T(w.Connection, "Complete", "High"));
        await w.H.Db.SaveChangesAsync();

        (await w.Service.MappingHealthAsync(w.Connection)).Level.Should().Be("Blocking");

        // Everything tickets hold and everything the PSA lists, mapped: nothing left but the optional.
        w.H.Db.FieldMappings.AddRange(new[] { "New", "In Progress", "Waiting Customer", "Resolved", "Closed", "Complete" }
            .Select(s => Rule(w.Connection, "status", s, s is "Closed" or "Complete" ? "CLOSED" : s is "Resolved" ? "RESOLVED" : s is "New" ? "NEW" : s is "In Progress" ? "IN_PROGRESS" : "WAITING_CUSTOMER")));
        w.H.Db.FieldMappings.AddRange(new[] { ("Low", "LOW"), ("Medium", "NORMAL"), ("High", "HIGH"), ("Critical", "CRITICAL") }
            .Select(p => Rule(w.Connection, "priority", p.Item1, p.Item2)));
        w.H.Db.FieldMappings.Add(new FieldMapping
        {
            MspOrganizationId = Org, Provider = ProviderType.ConnectWisePsa, Scope = MappingScope.ConnectionOverride, PsaConnectionId = w.Connection,
            PortalField = "status", ExternalField = "status", PortalValue = "ON_HOLD", ExternalValue = "Waiting Customer", Direction = MappingDirection.PortalToProvider,
        });
        await w.H.Db.SaveChangesAsync();

        var mapped = await w.Service.MappingHealthAsync(w.Connection);

        mapped.Fields.Should().OnlyContain(f => f.Mapped == f.Values && f.UnmappedTickets == 0);
        mapped.OutboundStatuses.Should().OnlyContain(o => o.Problem == null);
        mapped.Level.Should().Be("Optional", "the mock PSA lists a technician nobody here is linked to: that can be done and need not be");
    }

    [Fact]
    public async Task A_portal_status_with_no_PSA_counterpart_is_said_before_someone_tries_to_set_it()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.FieldMappings.AddRange(
            Rule(w.Connection, "status", "New", "NEW"),
            // Mapped to something this PSA does not list: saved without complaint, refused on every write.
            Rule(w.Connection, "status", "Done", "RESOLVED"));
        await w.H.Db.SaveChangesAsync();

        var outbound = (await w.Service.MappingHealthAsync(w.Connection)).OutboundStatuses;

        outbound.Select(o => o.PortalValue).Should().Equal(PortalVocabulary.Statuses);
        outbound.Single(o => o.PortalValue == "NEW").Should().Be(new OutboundStatusDto("NEW", "New", null));
        outbound.Single(o => o.PortalValue == "IN_PROGRESS").Problem.Should().StartWith("Nothing maps it to a status in the PSA");
        outbound.Single(o => o.PortalValue == "RESOLVED").Should().Be(
            new OutboundStatusDto("RESOLVED", "Done", "It is sent as \"Done\", which the PSA does not list."));
    }

    [Fact]
    public async Task Health_is_still_given_when_the_PSA_cannot_be_asked()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.Tickets.Add(T(w.Connection, "Complete", "High"));
        await w.H.Db.SaveChangesAsync();
        w.Psa.Unreachable = true;

        var health = await w.Service.MappingHealthAsync(w.Connection);

        health.Fields.Single(f => f.Field == "status").Items.Select(i => i.Value).Should().Equal("Complete");
        health.Notes.Should().ContainSingle().Which.Should().Contain("could not be read just now");
    }

    [Fact]
    public async Task Technicians_the_portal_does_not_know_as_its_own_people_are_listed_and_the_account_it_writes_as_is_not()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        var user = new AppUser { MspOrganizationId = Org, Email = "bilal@techpio.test", DisplayName = "Bilal Khan" };
        w.H.Db.AppUsers.Add(user);
        w.H.Db.UserPsaIdentities.Add(new UserPsaIdentity { MspOrganizationId = Org, AppUserId = user.Id, PsaConnectionId = w.Connection, ExternalTechnicianId = "43" });
        (await w.H.Db.PsaConnections.SingleAsync(c => c.Id == w.Connection)).DefaultTimeEntryResourceId = "api";
        w.H.Db.Tickets.AddRange(
            T(w.Connection, "New", "High", tech: "42", techName: "Asha Rao"), T(w.Connection, "New", "High", tech: "42", techName: "Asha Rao"),
            T(w.Connection, "New", "High", tech: "43", techName: "Bilal Khan"),
            T(w.Connection, "New", "High", tech: "api", techName: "API User"));
        await w.H.Db.SaveChangesAsync();

        var people = (await w.Service.MappingHealthAsync(w.Connection)).Technicians;

        // Asha and Bilal hold tickets; the mock PSA also lists "Tech One", who holds none.
        (people.Technicians, people.Linked, people.Ignored).Should().Be((3, 1, 0));
        people.Unlinked.Select(p => (p.ExternalId, p.Name, p.Tickets)).Should().Equal(("42", "Asha Rao", 2), ("R-1", "Tech One", 0));

        // "Tech One" is an account nobody will ever sign in as: said once, it is no longer still to do.
        // An ignore for a login that is linked (Bilal's) changes nothing: linked is linked.
        w.H.Db.PsaTechnicianIgnores.AddRange(
            new PsaTechnicianIgnore { MspOrganizationId = Org, PsaConnectionId = w.Connection, ExternalTechnicianId = "r-1" },
            new PsaTechnicianIgnore { MspOrganizationId = Org, PsaConnectionId = w.Connection, ExternalTechnicianId = "43" },
            new PsaTechnicianIgnore { MspOrganizationId = Org, PsaConnectionId = w.Other, ExternalTechnicianId = "42" });
        await w.H.Db.SaveChangesAsync();

        var after = (await w.Service.MappingHealthAsync(w.Connection)).Technicians;

        (after.Technicians, after.Linked, after.Ignored).Should().Be((2, 1, 1));
        after.Unlinked.Select(p => p.ExternalId).Should().Equal(["42"], "ignored on another PSA account is not ignored on this one");
    }

    [Fact]
    public async Task A_rule_written_for_one_board_is_applied_to_that_boards_tickets_and_no_others()
    {
        // The same word can mean two things on one connection: "Complete" is closed on the
        // projects board and only resolved everywhere else. Tickets holding a value are rewritten
        // together when no rule is like that - and must not be when one is.
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        Ticket On(string? board)
        {
            var t = T(w.Connection, "Complete", "Medium");
            t.QueueOrBoard = board;
            return t;
        }
        var project = On("Projects");
        var desk = On("Service Desk");
        var nowhere = On(null);
        w.H.Db.Tickets.AddRange(project, desk, nowhere);
        var forProjects = Rule(w.Connection, "status", "Complete", "CLOSED");
        (forProjects.Scope, forProjects.QueueOrBoardKey) = (MappingScope.QueueOrBoardOverride, "Projects");
        w.H.Db.FieldMappings.AddRange(forProjects, Rule(w.Connection, "status", "Complete", "RESOLVED"));
        await w.H.Db.SaveChangesAsync();

        var applied = await w.Service.ApplyMappingAsync(w.Connection);

        applied.StatusesChanged.Should().Be(3);
        applied.Changes.Should().BeEquivalentTo(["status: Complete → RESOLVED (2)", "status: Complete → CLOSED (1)"]);
        async Task<string> NowAsync(Ticket t) => (await w.H.Db.Tickets.AsNoTracking().SingleAsync(x => x.Id == t.Id)).PortalStatus;
        (await NowAsync(project)).Should().Be("CLOSED");
        (await NowAsync(desk)).Should().Be("RESOLVED");
        (await NowAsync(nowhere)).Should().Be("RESOLVED");
        var health = await w.Service.MappingHealthAsync(w.Connection);
        Value(health, "status", "Complete").UnmappedTickets.Should().Be(0);
        health.UnmappedTickets.Should().Be(3, "the priority Medium still has no rule, on any of them");
    }

    [Fact]
    public async Task Applying_the_mapping_changes_only_tickets_still_showing_the_PSAs_own_word()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        var passedThrough = T(w.Connection, "Complete", "Medium");
        var setHere = T(w.Connection, "Complete", "Medium", portalStatus: "IN_PROGRESS", portalPriority: "HIGH");
        var elsewhere = T(w.Other, "Complete", "Medium");
        var stillUnmapped = T(w.Connection, "Vendor Hold", "Medium");
        w.H.Db.Tickets.AddRange(passedThrough, setHere, elsewhere, stillUnmapped);
        await w.H.Db.SaveChangesAsync();

        (await w.Service.ApplyMappingAsync(w.Connection)).Should().BeEquivalentTo(new MappingApplyResultDto(0, 0, []), "no rule maps anything yet");

        w.H.Db.FieldMappings.AddRange(Rule(w.Connection, "status", "Complete", "CLOSED"), Rule(w.Connection, "priority", "Medium", "NORMAL"));
        await w.H.Db.SaveChangesAsync();

        var applied = await w.Service.ApplyMappingAsync(w.Connection);

        (applied.StatusesChanged, applied.PrioritiesChanged).Should().Be((1, 2));
        applied.Changes.Should().BeEquivalentTo(["priority: Medium → NORMAL (2)", "status: Complete → CLOSED (1)"]);
        async Task<(string, string)> NowAsync(Ticket t)
        {
            var row = await w.H.Db.Tickets.AsNoTracking().SingleAsync(x => x.Id == t.Id);
            return (row.PortalStatus, row.PortalPriority);
        }
        (await NowAsync(passedThrough)).Should().Be(("CLOSED", "NORMAL"));
        (await NowAsync(setHere)).Should().Be(("IN_PROGRESS", "HIGH"), "someone set these in the portal: they are not the mapping's to rewrite");
        (await NowAsync(elsewhere)).Should().Be(("Complete", "Medium"), "another PSA account's ticket, under another account's rules");
        (await NowAsync(stillUnmapped)).Should().Be(("Vendor Hold", "NORMAL"));
        (await w.H.Db.Tickets.AsNoTracking().SingleAsync(x => x.Id == passedThrough.Id)).PsaStatus.Should().Be("Complete", "what the PSA said is kept as it said it");

        (await w.H.Db.AuditLog.SingleAsync(a => a.Action == "mapping.applied" && a.DetailJson!.Contains("Complete"))).EntityId.Should().Be(w.Connection.ToString());
        (await w.Service.ApplyMappingAsync(w.Connection)).Should().BeEquivalentTo(new MappingApplyResultDto(0, 0, []), "done once, there is nothing left to do");

        // And the health check agrees with what was just done.
        var health = await w.Service.MappingHealthAsync(w.Connection);
        Value(health, "status", "Complete").UnmappedTickets.Should().Be(0);
        Value(health, "status", "Vendor Hold").UnmappedTickets.Should().Be(1);
    }

    [Fact]
    public async Task The_preview_shows_tickets_already_here_and_reads_a_sample_from_the_PSA_only_when_there_are_none()
    {
        var w = await BuildAsync();
        await using var _ = w.H.Db;
        w.H.Db.FieldMappings.Add(Rule(w.Connection, "status", "New", "NEW"));
        await w.H.Db.SaveChangesAsync();
        await w.Psa.Mock.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "In the PSA only", ExternalCompanyId = "ORG-1", IdempotencyKey = "p-1" });

        var live = await w.Service.MappingPreviewAsync(w.Connection);

        live.Should().ContainSingle().Which.Should().Match<MappingPreviewRowDto>(r => r.Title == "In the PSA only" && r.ReadFromPsa);
        (await w.H.Db.Tickets.CountAsync()).Should().Be(0, "a preview keeps nothing");

        w.H.Db.Tickets.AddRange(T(w.Connection, "New", "High"), T(w.Connection, "Complete", "High"));
        await w.H.Db.SaveChangesAsync();

        var stored = await w.Service.MappingPreviewAsync(w.Connection);

        stored.Should().OnlyContain(r => !r.ReadFromPsa);
        stored.Select(r => (r.SourceStatus, r.MappedStatus)).Should().BeEquivalentTo([("New", "NEW"), ("Complete", (string?)null)]);
    }
}
