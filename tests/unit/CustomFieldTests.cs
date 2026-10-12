using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Application.Tickets;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A PSA's custom fields reach the portal only where an administrator chose them, and reach a
/// client only where that was said as well.
///
/// What these hold: that nothing of a field is stored until it is chosen; that a chosen field is
/// staff only by default and its value is not in what a client is sent, in any form; that who sees
/// a field is decided when the ticket is read, so taking a field away or back from clients is
/// immediate; that the choice is one connection's and one organization's; and that every change is
/// audited, with showing a field to clients named on its own. On a SQL translator.
///
/// The PSA here lists four fields: an asset tag, a cost centre, a note the engineers keep for
/// themselves, and whether the device is under warranty.
/// </summary>
public sealed class CustomFieldTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Elsewhere = Guid.NewGuid();
    private const string Secret = "DO-NOT-SHOW-THE-CLIENT: owner is difficult, quote high";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new();
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly DeskDbContext _platform;
    private readonly StubConnector _psa = new(ProviderType.AutotaskPsa);
    private Guid _main, _second, _theirs, _acme, _priya;

    /// <summary>The PSA, asked for its custom fields as the connection service asks it.</summary>
    private sealed class Resolver(IServiceManagementConnector c) : ICustomFieldSource
    {
        public async Task<(bool Supported, IReadOnlyList<ExternalFieldDefinition> Fields)> ListCustomFieldsAsync(Guid connectionId, CancellationToken ct = default)
            => (await c.GetCapabilitiesAsync(ct)).SupportsCustomFields ? (true, await c.GetCustomFieldsAsync(ct)) : (false, []);
    }

    public CustomFieldTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        var platform = new TenantContext();
        platform.SetPlatformScope();
        _platform = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, platform, _clock);

        PsaConnection Of(Guid org, string name) => new()
        {
            MspOrganizationId = org, Name = name, Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://" + name, IsEnabled = true,
        };
        var (main, second, theirs) = (Of(Org, "Main"), Of(Org, "Second account"), Of(Elsewhere, "Somebody else's"));
        (_main, _second, _theirs) = (main.Id, second.Id, theirs.Id);
        var acme = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = main.Id, Name = "Acme", ExternalCompanyId = "1" };
        var priya = new ClientUser { MspOrganizationId = Org, ClientCompanyId = acme.Id, DisplayName = "Priya Nair", Email = "priya@acme.test", IdpSubject = "priya", IsCompanyAdministrator = true };
        (_acme, _priya) = (acme.Id, priya.Id);
        _platform.AddRange(
            new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" },
            new MspOrganization { Id = Elsewhere, Name = "Another MSP", Slug = "another", TimeZone = "UTC" },
            main, second, theirs, acme, priya,
            new ClientCompany { MspOrganizationId = Org, PsaConnectionId = second.Id, Name = "Acme", ExternalCompanyId = "1" },
            new ClientCompany { MspOrganizationId = Elsewhere, PsaConnectionId = theirs.Id, Name = "Initech", ExternalCompanyId = "1" });
        _platform.SaveChanges();
        _platform.ChangeTracker.Clear();

        _psa.CustomFields.AddRange(
        [
            new ExternalFieldDefinition("AssetTag", "Asset tag", CustomFieldTypes.Text, false),
            new ExternalFieldDefinition("CostCentre", "Cost centre", CustomFieldTypes.List, false),
            new ExternalFieldDefinition("EngineerNote", "Notes for the engineer", CustomFieldTypes.Text, false),
            new ExternalFieldDefinition("UnderWarranty", "Under warranty", CustomFieldTypes.Boolean, false),
        ]);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
        _connection.Dispose();
    }

    private CustomFieldService Settings()
        => new(_db, new Resolver(_psa), new Stepping(new AuditWriter(_db, new TestCurrentUser(Org, name: "Dana Desk"), _tenant, _clock), _clock));

    private sealed class SwitchedOff : ICustomFieldSource
    {
        public Task<(bool Supported, IReadOnlyList<ExternalFieldDefinition> Fields)> ListCustomFieldsAsync(Guid connectionId, CancellationToken ct = default)
            => throw new ValidationFailedException("PSA connection 'Main' is disabled.");
    }

    private sealed class Stepping(IAuditWriter inner, TestClock clock) : IAuditWriter
    {
        public async Task WriteAsync(string action, string entityType, string? entityId, object? detail = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(action, entityType, entityId, detail, ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private TicketSyncService Sync()
    {
        _platform.ChangeTracker.Clear();
        return new(_platform, new MappingEngine(), new SyncEventStore(_platform, _clock), _clock, new RecordingActivity());
    }

    /// <summary>A ticket as the PSA sends it, with every one of its custom fields.</summary>
    private static UnifiedTicket Sent(string id = "500", string? asset = "LT-0042") => new()
    {
        ExternalId = id, Title = "Laptop will not charge", Status = "New", Priority = "High",
        RequesterExternalId = "1", RequesterName = "Acme", RequesterEmail = "a@acme.test",
        CustomFields = new Dictionary<string, string?>
        {
            ["AssetTag"] = asset, ["CostCentre"] = "Finance", ["EngineerNote"] = Secret, ["UnderWarranty"] = "true", ["Unlisted"] = "something else",
        },
    };

    private async Task<Ticket> TicketAsync(string externalId = "500", Guid? connection = null)
        => await _platform.Tickets.AsNoTracking().SingleAsync(t => t.PsaConnectionId == (connection ?? _main) && t.ExternalTicketId == externalId);

    private TicketReadService Reads() => new(_db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));

    private async Task<(TicketDetailDto Staff, TicketDetailDto Client)> ReadAsync(string externalId = "500")
    {
        _db.ChangeTracker.Clear();
        var ticket = await TicketAsync(externalId);
        // The client is the company's administrator: the widest a client's view of a ticket gets.
        return ((await Reads().GetDetailForStaffAsync(ticket.Id))!, (await Reads().GetDetailAsync(new ClientAccess(Org, _acme, _priya, true), ticket.Id))!);
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private async Task<List<string>> AuditedAsync()
        => await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("customfields.")).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => a.Action + " " + a.DetailJson).ToListAsync();

    // ------------------------------------------------------------------ nothing until chosen

    [Fact]
    public async Task Nothing_of_a_custom_field_is_kept_until_somebody_chooses_it()
    {
        var settings = await Settings().GetAsync(_main);
        settings.Should().BeEquivalentTo(new { Supported = true, Imported = 0, ClientVisible = 0 });
        settings.Fields.Select(f => (f.Key, f.Label, f.DataType, f.Import, f.ClientVisible, f.Editable)).Should().BeEquivalentTo(new[]
        {
            ("AssetTag", "Asset tag", "text", false, false, false), ("CostCentre", "Cost centre", "list", false, false, false),
            ("EngineerNote", "Notes for the engineer", "text", false, false, false), ("UnderWarranty", "Under warranty", "boolean", false, false, false),
        }, "the fields are the PSA's own, and none is chosen for anybody");

        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.Created);

        (await TicketAsync()).CustomFieldsJson.Should().BeNull("the PSA sent five values and none was asked for");
        var (staff, client) = await ReadAsync();
        (staff.CustomFields, client.CustomFields).Should().Be((null, null));
        (await _platform.PsaCustomFields.CountAsync()).Should().Be(0, "looking at the list decides nothing");
        // And a ticket is as it was: read again, nothing to do.
        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.SkippedUnchanged);
    }

    [Fact]
    public async Task A_chosen_field_is_for_staff_only_until_it_is_said_to_be_the_clients_too()
    {
        var saved = await Settings().SaveAsync(_main,
        [
            new SetCustomFieldInput("AssetTag", Import: true),
            new SetCustomFieldInput("EngineerNote", Import: true, PortalLabel: "Engineer's note"),
            new SetCustomFieldInput("CostCentre", Import: false),
        ]);
        saved.Should().BeEquivalentTo(new { Imported = 2, ClientVisible = 0 });
        saved.Fields.Where(f => f.Import).Select(f => (f.Key, f.PortalLabel, f.ClientVisible)).Should().BeEquivalentTo(new[]
        {
            ("AssetTag", "Asset tag", false), ("EngineerNote", "Engineer's note", false),
        }, "nobody said the client may see either");

        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.Created);

        var held = CustomFieldValues.Read((await TicketAsync()).CustomFieldsJson);
        held.Should().BeEquivalentTo(new Dictionary<string, string> { ["AssetTag"] = "LT-0042", ["EngineerNote"] = Secret },
            "only the two that were chosen are stored: not the one set to ignore, the one nobody decided about, or one the PSA does not list");
        var (staff, client) = await ReadAsync();
        staff.CustomFields.Should().BeEquivalentTo(new[]
        {
            new TicketCustomFieldDto("Asset tag", "LT-0042", "text", false), new TicketCustomFieldDto("Engineer's note", Secret, "text", false),
        }, o => o.WithStrictOrdering());
        client.CustomFields.Should().BeNull();
        Json(client).Should().NotContain("LT-0042").And.NotContain("DO-NOT-SHOW").And.NotContain("Engineer", "nothing of a staff-only field is in what a client is sent");

        // The asset tag is said to be the client's to see. The engineers' note is not.
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", Import: true, ClientVisible: true)]);
        (staff, client) = await ReadAsync();
        client.CustomFields.Should().BeEquivalentTo(new[] { new TicketCustomFieldDto("Asset tag", "LT-0042", "text", true) });
        staff.CustomFields.Should().HaveCount(2).And.Contain(f => f.Label == "Asset tag" && f.ClientVisible).And.Contain(f => f.Label == "Engineer's note" && !f.ClientVisible);
        Json(client).Should().Contain("LT-0042").And.NotContain("DO-NOT-SHOW").And.NotContain("Engineer");
    }

    [Fact]
    public async Task Who_sees_a_field_is_decided_when_the_ticket_is_read_so_taking_it_back_is_immediate()
    {
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, null, true), new SetCustomFieldInput("EngineerNote", true)]);
        await Sync().UpsertFromProviderAsync(_main, Sent(), []);
        (await ReadAsync()).Client.CustomFields.Should().ContainSingle();

        // Taken back from clients. No sync runs: the value is still stored, and the client is no longer sent it.
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, null, false)]);
        var (staff, client) = await ReadAsync();
        (client.CustomFields, staff.CustomFields!.Count).Should().Be((null, 2));
        Json(client).Should().NotContain("LT-0042");

        // Set to ignore. Still no sync: it is shown to nobody, staff included, though the row has not been read again yet.
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("EngineerNote", false)]);
        (await TicketAsync()).CustomFieldsJson.Should().Contain("DO-NOT-SHOW", "the stored copy goes when the ticket is next read");
        (await ReadAsync()).Staff.CustomFields.Should().ContainSingle().Which.Label.Should().Be("Asset tag");

        // The next read of the ticket, exactly as the PSA sent it before, is not "unchanged": what is kept has changed.
        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.Updated);
        (await TicketAsync()).CustomFieldsJson.Should().NotContain("DO-NOT-SHOW").And.Contain("LT-0042");
        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.SkippedUnchanged);
    }

    [Fact]
    public async Task A_field_chosen_after_a_ticket_arrived_reaches_it_the_next_time_it_is_read_and_follows_the_PSA()
    {
        await Sync().UpsertFromProviderAsync(_main, Sent(), []);
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true), new SetCustomFieldInput("UnderWarranty", true)]);
        (await ReadAsync()).Staff.CustomFields.Should().BeNull("the ticket has not been read since");

        (await Sync().UpsertFromProviderAsync(_main, Sent(), [])).Should().Be(TicketSyncOutcome.Updated);
        (await ReadAsync()).Staff.CustomFields.Should().BeEquivalentTo(new[]
        {
            new TicketCustomFieldDto("Asset tag", "LT-0042", "text", false), new TicketCustomFieldDto("Under warranty", "true", "boolean", false),
        });

        // Changed in the PSA, then cleared there.
        (await Sync().UpsertFromProviderAsync(_main, Sent(asset: "LT-0099"), [])).Should().Be(TicketSyncOutcome.Updated);
        (await ReadAsync()).Staff.CustomFields.Should().Contain(f => f.Label == "Asset tag" && f.Value == "LT-0099");
        (await Sync().UpsertFromProviderAsync(_main, Sent(asset: "  "), [])).Should().Be(TicketSyncOutcome.Updated);
        (await ReadAsync()).Staff.CustomFields.Should().ContainSingle().Which.Label.Should().Be("Under warranty");
    }

    // ------------------------------------------------------------------ whose choice it is

    [Fact]
    public async Task A_choice_is_one_connections_and_another_organizations_connection_is_not_found()
    {
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, null, true)]);
        var sync = Sync();
        await sync.UpsertFromProviderAsync(_main, Sent(), []);
        await sync.UpsertFromProviderAsync(_second, Sent(), []);
        await sync.UpsertFromProviderAsync(_theirs, Sent(), []);

        (await TicketAsync(connection: _main)).CustomFieldsJson.Should().Contain("LT-0042");
        (await TicketAsync(connection: _second)).CustomFieldsJson.Should().BeNull("the same field on another account was not chosen there");
        (await TicketAsync(connection: _theirs)).CustomFieldsJson.Should().BeNull();
        (await Settings().GetAsync(_second)).Imported.Should().Be(0);

        await FluentActions.Awaiting(() => Settings().GetAsync(_theirs)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Settings().SaveAsync(_theirs, [new SetCustomFieldInput("AssetTag", true, null, true)])).Should().ThrowAsync<NotFoundException>();
        (await _platform.PsaCustomFields.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { MspOrganizationId = Org, PsaConnectionId = _main, ExternalKey = "AssetTag" });
    }

    [Fact]
    public async Task Everything_wrong_with_a_save_is_said_at_once_and_none_of_it_is_saved()
    {
        var act = () => Settings().SaveAsync(_main,
        [
            new SetCustomFieldInput("AssetTag", Import: true),
            new SetCustomFieldInput("Invented", Import: true),
            new SetCustomFieldInput("CostCentre", Import: false, ClientVisible: true),
            new SetCustomFieldInput("EngineerNote", Import: true, PortalLabel: "asset TAG"),
            new SetCustomFieldInput("UnderWarranty", Import: true, PortalLabel: new string('x', 201)),
            new SetCustomFieldInput("AssetTag", Import: false),
        ]);

        var refused = (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message;
        refused.Should().Contain("The PSA does not list a custom field Invented")
            .And.Contain("Cost centre is set to be shown to clients and also to be ignored")
            .And.Contain("More than one imported field would be called")
            .And.Contain("longer than 200 characters")
            .And.Contain("The field AssetTag is in the list twice");
        (await _platform.PsaCustomFields.CountAsync()).Should().Be(0);
        (await AuditedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_change_is_audited_and_showing_a_field_to_clients_is_named_on_its_own()
    {
        var settings = Settings();
        await settings.SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true), new SetCustomFieldInput("EngineerNote", true)]);
        await settings.SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true), new SetCustomFieldInput("EngineerNote", true)]);   // as it is: nothing to record
        await settings.SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, "Device tag", true)]);
        await settings.SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, "Device tag", false), new SetCustomFieldInput("EngineerNote", false)]);

        var audited = await AuditedAsync();
        audited.Select(a => a.Split(' ')[0]).Should().Equal("customfields.changed", "customfields.changed", "customfields.shown_to_clients", "customfields.changed");
        audited[0].Should().Contain("\"imported\":[\"Asset tag\",\"Notes for the engineer\"]").And.Contain("\"shownToClients\":[]");
        audited[1].Should().Contain("\"shownToClients\":[\"Device tag\"]").And.Contain("Asset tag \\u2192 Device tag");
        audited[2].Should().Contain("\"fields\":[\"Device tag\"]");
        audited[3].Should().Contain("\"hiddenFromClients\":[\"Device tag\"]").And.Contain("\"ignored\":[\"Notes for the engineer\"]");
        (await _db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith("customfields.")).Select(a => a.ActorDisplayName).Distinct().ToListAsync()).Should().Equal("Dana Desk");
    }

    [Fact]
    public async Task While_the_PSA_cannot_be_asked_a_decision_can_be_undone_and_a_new_field_cannot_be_chosen_blind()
    {
        await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, null, true)]);
        _psa.CustomFieldFailure = new ConnectorException(ConnectorFailureKind.Timeout, "The PSA did not answer.");

        var settings = await Settings().GetAsync(_main);
        settings.Notes.Should().ContainSingle().Which.Should().Contain("could not be read just now").And.Contain("decisions already saved");
        settings.Fields.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Key = "AssetTag", Label = "Asset tag", ListedByPsa = false, Import = true, ClientVisible = true });

        (await FluentActions.Awaiting(() => Settings().SaveAsync(_main, [new SetCustomFieldInput("CostCentre", true)])).Should().ThrowAsync<ValidationFailedException>())
            .Which.Message.Should().Contain("cannot be asked about it just now");
        // Nor while the portal itself will not ask, because the connection is switched off: said in the portal's own words.
        _psa.CustomFieldFailure = null;
        var off = new CustomFieldService(_db, new SwitchedOff(), new AuditWriter(_db, new TestCurrentUser(Org), _tenant, _clock));
        (await off.GetAsync(_main)).Notes.Should().ContainSingle().Which.Should().Contain("was not asked").And.Contain("PSA connection 'Main' is disabled.");
        _psa.CustomFieldFailure = new ConnectorException(ConnectorFailureKind.Timeout, "The PSA did not answer.");
        // Taking a field back from clients does not wait for the PSA.
        (await Settings().SaveAsync(_main, [new SetCustomFieldInput("AssetTag", true, null, false)])).ClientVisible.Should().Be(0);
    }

    [Fact]
    public async Task A_connector_that_reads_no_custom_fields_says_so_and_offers_none()
    {
        var none = new StubConnector(ProviderType.ConnectWisePsa);
        var settings = await new CustomFieldService(_db, new Resolver(none), new AuditWriter(_db, new TestCurrentUser(Org), _tenant, _clock)).GetAsync(_main);

        settings.Should().BeEquivalentTo(new { Supported = false, Imported = 0 });
        settings.Fields.Should().BeEmpty();
        settings.Notes.Should().ContainSingle().Which.Should().Contain("does not read custom fields");
    }

    // ------------------------------------------------------------------ what is stored

    [Fact]
    public void What_is_kept_is_only_what_was_chosen_and_has_a_value_written_the_same_way_every_time()
    {
        var sent = new Dictionary<string, string?> { ["b"] = " two ", ["a"] = "one", ["c"] = null, ["d"] = "   ", ["e"] = "not chosen" };
        var chosen = new HashSet<string>(["a", "b", "c", "d"]);

        CustomFieldValues.Keep(sent, chosen).Should().Be("{\"a\":\"one\",\"b\":\"two\"}");
        CustomFieldValues.Keep(new Dictionary<string, string?> { ["a"] = "one", ["b"] = "two" }, chosen).Should().Be(CustomFieldValues.Keep(sent, chosen), "the order the PSA sends fields in is not a change");
        CustomFieldValues.Keep(sent, new HashSet<string>()).Should().BeNull();
        CustomFieldValues.Keep(new Dictionary<string, string?>(), chosen).Should().BeNull();
        CustomFieldValues.Keep(new Dictionary<string, string?> { ["a"] = new string('x', 5000) }, chosen)!.Length.Should().BeLessThan(2100, "one field cannot fill the row");
        CustomFieldValues.Read("{\"a\":\"one\"}").Should().ContainKey("a");
        CustomFieldValues.Read(null).Should().BeEmpty();
        CustomFieldValues.Read("not json").Should().BeEmpty("a value that cannot be read is nothing, not an error on every ticket page");
    }
}
