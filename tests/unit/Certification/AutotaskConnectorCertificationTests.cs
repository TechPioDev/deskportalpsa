using System.Net;
using Desk.Connectors.Autotask;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// Runs the shared certification suite against the REAL AutotaskConnector, driven by an in-memory
/// fake Autotask server. This certifies request construction, JSON parsing, and HTTP error mapping
/// end-to-end. Same tests as the mock — proving cross-provider contract compliance.
/// </summary>
public sealed class AutotaskConnectorCertificationTests : ConnectorCertificationSuite
{
    private const string Secret = "at-webhook-secret";

    private AutotaskConnector Build(FakeAutotaskServer server, string webhookSecret = Secret)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://webservices.local/atservicesrest/") };
        var config = new AutotaskConnectorConfig
        {
            BaseUrl = "https://webservices.local/atservicesrest/",
            Credentials = new AutotaskCredentials("code", "user", "secret"),
            WebhookSecret = webhookSecret,
        };
        return new AutotaskConnector(http, config, Clock);
    }

    protected override IServiceManagementConnector CreateConnector() => Build(new FakeAutotaskServer(Clock));

    protected override IServiceManagementConnector CreateConnectorWithoutWebhookSecret() => Build(new FakeAutotaskServer(Clock), "");

    [Fact]
    public async Task A_ticket_carries_its_contact_resolved_from_the_contact_id()
    {
        // Only contactID rides on an Autotask ticket. Without resolving it no ticket knew who it was
        // for, and the portal could not tell whether a public reply would reach anyone.
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var withContact = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "Mailbox full", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "c1" });
        var withoutContact = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "Monitoring alert", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "c2" });
        server.SetTicketContact(long.Parse(withContact.ExternalId!), 10);

        var page = await c.GetTicketsAsync(new TicketFilter());
        var a = page.Items.Single(t => t.ExternalId == withContact.ExternalId);
        var b = page.Items.Single(t => t.ExternalId == withoutContact.ExternalId);

        (a.RequesterName, a.RequesterEmail).Should().Be(("Acme User", "user@acme.test"));
        (b.RequesterName, b.RequesterEmail).Should().Be(((string?)null, (string?)null));
        (await c.GetTicketAsync(withContact.ExternalId!))!.RequesterEmail.Should().Be("user@acme.test");
    }

    [Fact]
    public async Task A_clients_configuration_items_arrive_as_named_typed_devices()
    {
        var c = Build(new FakeAutotaskServer(Clock));

        var devices = await c.GetDevicesAsync(SeededOrganizationId);

        // Only company 1's: GLOBEX-FW belongs to company 2.
        devices.Should().HaveCount(2);
        devices.Should().ContainEquivalentOf(new
        {
            ExternalId = "70", Name = "ACME-SRV01", Type = "Server", Identifier = "7XK29", IsActive = true,
            WarrantyExpiresAt = (DateTimeOffset?)DateTimeOffset.Parse("2027-03-31T00:00:00Z"),
        });
        // No title: named by its serial rather than by an id. Its type id has no label in the
        // tenant's metadata, so it has no type - never the bare number.
        devices.Should().ContainEquivalentOf(new { ExternalId = "71", Name = "OLD-LAPTOP-9", Type = (string?)null, IsActive = false });
    }

    [Fact]
    public async Task A_ticket_carries_the_device_it_is_about_and_says_when_it_has_none()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var about = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "Disk failing", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "d1" });
        var general = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "New starter", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "d2" });
        server.SetTicketConfigurationItem(long.Parse(about.ExternalId!), 70);

        var page = await c.GetTicketsAsync(new TicketFilter());

        page.Items.Single(t => t.ExternalId == about.ExternalId).Should().BeEquivalentTo(new { DeviceExternalId = "70", DeviceKnown = true });
        // Autotask always says: no device is a real "none", which lets the sync clear one removed there.
        page.Items.Single(t => t.ExternalId == general.ExternalId).Should().BeEquivalentTo(new { DeviceExternalId = (string?)null, DeviceKnown = true });
    }

    [Fact]
    public async Task A_ticket_can_be_raised_about_a_device_and_moved_to_another_or_to_none()
    {
        var c = Build(new FakeAutotaskServer(Clock));

        var t = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "Disk failing", ExternalCompanyId = SeededOrganizationId, DeviceExternalId = "70", IdempotencyKey = "dv1" });
        (await c.GetTicketAsync(t.ExternalId!))!.DeviceExternalId.Should().Be("70");

        await c.SetTicketDeviceAsync(t.ExternalId!, "71", previousDeviceExternalId: "70");
        (await c.GetTicketAsync(t.ExternalId!))!.DeviceExternalId.Should().Be("71");

        await c.SetTicketDeviceAsync(t.ExternalId!, null, previousDeviceExternalId: "71");
        (await c.GetTicketAsync(t.ExternalId!))!.Should().BeEquivalentTo(new { DeviceExternalId = (string?)null, DeviceKnown = true });
    }

    protected override IServiceManagementConnector CreateFailingConnector(ConnectorFailureKind kind)
    {
        var status = kind switch
        {
            ConnectorFailureKind.Authentication => HttpStatusCode.Unauthorized,
            ConnectorFailureKind.PermissionDenied => HttpStatusCode.Forbidden,
            ConnectorFailureKind.RateLimited => (HttpStatusCode)429,
            _ => HttpStatusCode.InternalServerError,
        };
        return Build(new FakeAutotaskServer(Clock) { ForceStatus = status });
    }

    protected override string SeededOrganizationId => "1";

    /// <summary>
    /// The live failure this pins: changing a ticket's status from the portal sent the LABEL
    /// ("In Progress") into a numeric picklist field, and Autotask answered
    /// HTTP 500 "Could not convert string to integer". Labels must resolve to the tenant's own
    /// picklist ids on the way out — and ids must resolve back to labels on the way in, or the
    /// portal shows the user a status of "1".
    /// </summary>
    [Fact]
    public async Task Picklist_labels_resolve_to_ids_outbound_and_back_to_labels_inbound()
    {
        var c = CreateConnector();
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "picklist round trip",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ExternalCompanyId = SeededOrganizationId,
            Status = "New",              // label, not an id
            Priority = "High",
            QueueOrBoard = "Service Desk",
        });
        created.Success.Should().BeTrue("a label must not reach Autotask as a string");

        // Inbound: the provider holds numeric ids; the portal must receive the tenant's labels.
        var read = await c.GetTicketAsync(created.ExternalId!);
        read!.Status.Should().Be("New");
        read.Priority.Should().Be("High");
        read.QueueOrBoard.Should().Be("Service Desk");

        // Outbound update, the exact path that failed in production.
        var updated = await c.UpdateTicketAsync(created.ExternalId!,
            new UnifiedTicketUpdate { Status = "Complete", IdempotencyKey = "k1" });
        updated.Success.Should().BeTrue();
        (await c.GetTicketAsync(created.ExternalId!))!.Status.Should().Be("Complete");

        // An id given directly still works — mappings that already store ids must keep working.
        (await c.UpdateTicketAsync(created.ExternalId!,
            new UnifiedTicketUpdate { Status = "1", IdempotencyKey = "k2" })).Success.Should().BeTrue();
        (await c.GetTicketAsync(created.ExternalId!))!.Status.Should().Be("New");
    }

    /// <summary>
    /// The live failure this pins: an admin configured time-entry technician "Autotask
    /// Administrator" with work role "Help Desk" — a pairing that resource does not hold — and
    /// every entry came back HTTP 500 "The specified AssignedResourceID and AssignedRoleID
    /// combination is not currently defined". Autotask does not accept a technician and a role
    /// independently; the PAIR must exist. The connector must use a role the resource actually
    /// holds rather than trusting a configured one that can never work.
    /// </summary>
    [Fact]
    public async Task A_configured_role_the_technician_does_not_hold_is_corrected_to_one_they_do()
    {
        var server = new FakeAutotaskServer(Clock);
        // Resource 20 holds role 55 only. The connection is configured with role 999 — plausible
        // to a human reading a role list, impossible for Autotask.
        var c = BuildWithTimeDefaults(server, resourceId: 20, roleId: 999);

        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "time", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        var result = await c.AddTimeEntryAsync(created.ExternalId!,
            new UnifiedTimeEntryCreateRequest(1m, null, null, BillableOption.Billable, "ok good work team", null));

        result.Success.Should().BeTrue("a valid pairing exists and must be used rather than losing the entry");
        server.TimeEntries.Should().ContainSingle()
            .Which["roleID"].Should().Be(55L, "the role the resource actually holds, not the configured one");
    }

    /// <summary>
    /// The readiness check exists so a misconfiguration is found on the settings page instead of
    /// when a technician's logged hour is rejected. It must name the technician, name the roles
    /// they really hold, and never write anything.
    /// </summary>
    [Fact]
    public async Task Time_entry_readiness_reports_the_real_pairing_without_writing_an_entry()
    {
        var server = new FakeAutotaskServer(Clock);

        // Configured with a role the technician does not hold — the live situation.
        var bad = await BuildWithTimeDefaults(server, resourceId: 20, roleId: 999).CheckTimeEntryReadinessAsync();
        bad.Ready.Should().BeFalse();
        bad.Summary.Should().Contain("Tech One").And.Contain("does not hold");
        bad.Remedies.Should().NotBeEmpty();
        bad.AvailableRoles.Should().NotBeEmpty("the admin needs the real options to choose from");

        // Configured correctly.
        var good = await BuildWithTimeDefaults(server, resourceId: 20, roleId: 55).CheckTimeEntryReadinessAsync();
        good.Ready.Should().BeTrue();
        good.Summary.Should().Contain("Tech One");

        // Nothing configured at all.
        var none = await BuildWithTimeDefaults(server, resourceId: 0, roleId: null).CheckTimeEntryReadinessAsync();
        none.Ready.Should().BeFalse();
        none.Summary.Should().Contain("No time-entry technician");

        server.TimeEntries.Should().BeEmpty("a readiness CHECK must never create a time entry");
    }

    /// <summary>
    /// The live failure this pins: 15 minutes logged with no notes came back
    /// "TimeEntry.summaryNotes can not be blank." Autotask mandates the field; ConnectWise does
    /// not, so the portal accepted the entry and the PSA refused it. The technician's time must
    /// survive the difference.
    /// </summary>
    [Fact]
    public async Task Time_logged_without_notes_still_reaches_autotask()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = BuildWithTimeDefaults(server, resourceId: 20, roleId: 55);
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });

        var result = await c.AddTimeEntryAsync(created.ExternalId!,
            new UnifiedTimeEntryCreateRequest(0.25m, null, null, BillableOption.Billable, "   ", null));

        result.Success.Should().BeTrue("blank notes are a portal-side gap, not a reason to lose the time");
        server.TimeEntries.Should().ContainSingle()
            .Which["summaryNotes"].ToString().Should().NotBeNullOrWhiteSpace();

        // Real notes must still travel through untouched.
        await c.AddTimeEntryAsync(created.ExternalId!,
            new UnifiedTimeEntryCreateRequest(0.5m, null, null, BillableOption.Billable, "Rebuilt the mail profile.", null));
        server.TimeEntries[1]["summaryNotes"].Should().Be("Rebuilt the mail profile.");
    }

    /// <summary>When the resource holds no role at all, say so — that is a real Autotask setup gap.</summary>
    [Fact]
    public async Task A_technician_with_no_active_role_is_reported_as_the_setup_problem_it_is()
    {
        var server = new FakeAutotaskServer(Clock);
        server.ResourceRoles.Clear();
        var c = BuildWithTimeDefaults(server, resourceId: 20, roleId: null);

        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "time", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        var act = async () => await c.AddTimeEntryAsync(created.ExternalId!,
            new UnifiedTimeEntryCreateRequest(1m, null, null, BillableOption.Billable, "n", null));

        (await act.Should().ThrowAsync<ConnectorException>()).Which.Message
            .Should().Contain("holds no active work role");
    }

    private AutotaskConnector BuildWithTimeDefaults(FakeAutotaskServer server, long resourceId, long? roleId)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://at.local/ATServicesRest/") };
        return new AutotaskConnector(http, new AutotaskConnectorConfig
        {
            BaseUrl = "https://at.local/ATServicesRest/",
            Credentials = new AutotaskCredentials("code", "user", "secret"),
            WebhookSecret = Secret,
            DefaultTimeEntryResourceId = resourceId,
            DefaultTimeEntryRoleId = roleId,
        }, Clock);
    }

    /// <summary>
    /// A portal status with no Autotask counterpart must say WHAT to do. Autotask's own answer is
    /// HTTP 500 "Could not convert string to integer", which tells the reader nothing; the connector
    /// names the tenant's real options instead, because the fix is a field mapping.
    /// </summary>
    [Fact]
    public async Task An_unmappable_status_names_the_tenants_real_options()
    {
        var c = CreateConnector();
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });

        var act = async () => await c.UpdateTicketAsync(created.ExternalId!,
            new UnifiedTicketUpdate { Status = "Awaiting Parts From Vendor", IdempotencyKey = "k2" });

        var ex = (await act.Should().ThrowAsync<ConnectorException>()).Which;
        ex.Kind.Should().Be(ConnectorFailureKind.InvalidRequest);
        ex.Message.Should().Contain("New").And.Contain("Complete", "the reader needs the real options to map onto");
        ex.Message.Should().NotContain("convert string to integer", "that is the message this replaces");
    }
    /// <summary>
    /// Autotask prints a note's title above its description, so a title taken as 250 characters of
    /// the body made one note read as two — the opening paragraph in bold, then again below.
    /// Reported from production. The title must be a heading, not a copy of the paragraph under it.
    /// </summary>
    [Fact]
    public async Task A_long_note_gets_a_short_heading_not_a_second_copy_of_itself()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var ticket = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        const string body =
            "we already testing every task from my side didn't surface the dead-letter count in the UI. "
            + "The state exists and is countable, but nothing displays it yet, so an operator cannot "
            + "currently see that OCR has given up on N screenshots without querying.";

        await c.AddPublicNoteAsync(ticket.ExternalId!, new UnifiedTicketNoteCreateRequest(body, IsPublic: true, "k2"));

        var title = server.LastNoteTitle!;
        title.Length.Should().BeLessThanOrEqualTo(81, "a heading, not a paragraph (80 chars plus the ellipsis)");
        title.Should().EndWith("…");
        title.Should().NotEndWith(" …", "the boundary trim should not leave a dangling space");
        // Cut on a word boundary: the last word before the ellipsis must be whole.
        body.Should().Contain(title.TrimEnd('…'), "the heading is an excerpt, not a re-wording");
        title.TrimEnd('…').Should().NotEndWith("didn'", "a mid-word cut is what made it look like broken duplicate text");
    }

    [Fact]
    public async Task A_short_note_is_its_own_title_unchanged()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var ticket = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });

        await c.AddPublicNoteAsync(ticket.ExternalId!,
            new UnifiedTicketNoteCreateRequest("Rebooted the switch, link is up.", IsPublic: true, "k2"));

        server.LastNoteTitle.Should().Be("Rebooted the switch, link is up.",
            "nothing to shorten, so nothing should be altered");
    }

    /// <summary>
    /// A note comes back with its author's resource id, not only the name. The name is just what the
    /// resource is called - and the integration account is called after a real person - so the portal
    /// can only tell the integration's notes from that person's by the id.
    /// </summary>
    [Fact]
    public async Task A_notes_author_carries_the_resource_id_not_only_its_name()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var ticket = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        await c.AddPublicNoteAsync(ticket.ExternalId!,
            new UnifiedTicketNoteCreateRequest("Rebooted the switch.", IsPublic: true, "k2"));

        var notes = await c.GetNotesAsync(ticket.ExternalId!);

        // The fake stamps creatorResourceID 20 on every note it creates, as Autotask does.
        notes.Should().ContainSingle(n => n.Body == "Rebooted the switch.")
            .Which.AuthorExternalId.Should().Be("20");
    }

    [Fact]
    public async Task An_attachments_author_carries_the_resource_id_too()
    {
        var server = new FakeAutotaskServer(Clock);
        var c = Build(server);
        var ticket = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        byte[] content = [1, 2, 3];
        await c.AddAttachmentAsync(ticket.ExternalId!, new SecureAttachment("a.bin", "application/octet-stream", 3, "k", content));

        var swept = (await c.GetRecentAttachmentsAsync(null)).Items;

        // The fake stamps attachedByResourceID 20 on every file it stores, as Autotask does.
        swept.Should().ContainSingle(r => r.TicketExternalId == ticket.ExternalId)
            .Which.Attachment.AuthorExternalId.Should().Be("20");
    }

    [Fact]
    public async Task A_ticket_carries_its_queues_id_beside_the_name()
    {
        // The name is what people read and map. The id is what Autotask is asked for, so it is what
        // a connection's queue limit holds - and with only the name on the ticket, that limit could
        // never be checked against what came back.
        var c = Build(new FakeAutotaskServer(Clock));
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Queued", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "q1", QueueOrBoard = "Service Desk",
        });

        var ticket = await c.GetTicketAsync(created.ExternalId!);

        (ticket!.QueueOrBoard, ticket.QueueOrBoardId).Should().Be(("Service Desk", "8"));
    }

    private AutotaskConnector BuildWithSweepLimits(FakeAutotaskServer server, int pageSize, int maxPages)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://webservices.local/atservicesrest/") };
        return new AutotaskConnector(http, new AutotaskConnectorConfig
        {
            BaseUrl = "https://webservices.local/atservicesrest/",
            Credentials = new AutotaskCredentials("code", "user", "secret"),
            WebhookSecret = Secret,
            AttachmentSweepPageSize = pageSize,
            AttachmentSweepMaxPages = maxPages,
        }, Clock);
    }

    private async Task<string> TicketWithFilesAsync(AutotaskConnector c, int files)
    {
        var ticket = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "t", IdempotencyKey = "k", ExternalCompanyId = SeededOrganizationId,
        });
        for (var i = 0; i < files; i++)
            await c.AddAttachmentAsync(ticket.ExternalId!,
                new SecureAttachment($"file-{i}.bin", "application/octet-stream", 3, $"k{i}", [1, 2, 3]));
        return ticket.ExternalId!;
    }

    [Fact]
    public async Task The_attachment_sweep_reads_every_page_and_says_the_list_is_complete()
    {
        // It read the first page only. Five files at two a page is three pages.
        var c = BuildWithSweepLimits(new FakeAutotaskServer(Clock), pageSize: 2, maxPages: 20);
        var ticketId = await TicketWithFilesAsync(c, 5);

        var sweep = await c.GetRecentAttachmentsAsync(null);

        sweep.Complete.Should().BeTrue();
        sweep.Items.Where(r => r.TicketExternalId == ticketId).Select(r => r.Attachment.FileName)
            .Should().BeEquivalentTo(["file-0.bin", "file-1.bin", "file-2.bin", "file-3.bin", "file-4.bin"]);
    }

    [Fact]
    public async Task The_attachment_sweep_says_so_when_it_stops_before_the_end()
    {
        // What it read is returned; what it must not do is call it everything, because the sync
        // deletes stored files that a complete list no longer contains.
        var c = BuildWithSweepLimits(new FakeAutotaskServer(Clock), pageSize: 2, maxPages: 2);
        await TicketWithFilesAsync(c, 5);

        var sweep = await c.GetRecentAttachmentsAsync(null);

        sweep.Complete.Should().BeFalse();
        sweep.Items.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_sweep_that_ends_exactly_on_its_last_allowed_page_is_complete()
    {
        var c = BuildWithSweepLimits(new FakeAutotaskServer(Clock), pageSize: 2, maxPages: 2);
        await TicketWithFilesAsync(c, 4);

        var sweep = await c.GetRecentAttachmentsAsync(null);

        sweep.Complete.Should().BeTrue();
        sweep.Items.Should().HaveCount(4);
    }

    protected override string WebhookSecret => Secret;

    /// <summary>
    /// A next-page URL is provider-supplied input, and following it carries the credentials along.
    /// If whatever answered the first request can redirect the second anywhere, pagination becomes a
    /// way to make this service fetch an attacker's URL with Autotask headers attached.
    /// </summary>
    [Fact]
    public async Task A_next_page_url_on_another_host_is_refused()
    {
        var server = new FakeAutotaskServer(Clock);
        var connector = Build(server);

        var act = async () => await connector.GetTicketsAsync(
            new TicketFilter { Cursor = "https://attacker.example/steal" });

        (await act.Should().ThrowAsync<ConnectorException>())
            .Which.Message.Should().Contain("different host");
    }

    [Fact]
    public async Task A_next_page_url_that_is_not_a_url_is_refused()
    {
        var connector = Build(new FakeAutotaskServer(Clock));

        var act = async () => await connector.GetTicketsAsync(new TicketFilter { Cursor = "../../etc/passwd" });

        await act.Should().ThrowAsync<ConnectorException>();
    }

    /// <summary>
    /// The value discovery offers has to be the value tickets arrive carrying.
    ///
    /// The mapping UI saves whatever discovery gives it as a rule's external value, and the sync
    /// compares that against the value on an incoming ticket. Autotask offered picklist IDS while
    /// ToUnifiedAsync resolves every one of those picklists to its LABEL on the way in, so a saved
    /// rule was compared against something it could never equal. Priority sat that way on all 101
    /// tickets in production: unmapped, showing Autotask's own value, indistinguishable from a
    /// mapping that passed it through deliberately.
    /// </summary>
    [Fact]
    public async Task A_status_rule_saved_from_discovery_can_match_an_incoming_ticket()
    {
        var connector = Build(new FakeAutotaskServer(Clock));
        var options = await connector.GetStatusesAsync();

        // Written with the value the provider is SENT — every one of these fields is a numeric
        // picklist and Autotask rejects a name outright.
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Printer", ExternalCompanyId = SeededOrganizationId,
            IdempotencyKey = "discovery-status", Status = options.First().Value,
        });
        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items.Single();

        // Read back as the value a rule has to match. Both representations, one round trip.
        options.Select(o => o.SyncValue).Should().Contain(ticket.Status,
            "a rule is saved with the sync value and compared against the ticket's");
        options.Should().OnlyContain(o => o.Value.All(char.IsDigit),
            "writes and filters send the picklist id");
    }

    [Fact]
    public async Task A_priority_rule_saved_from_discovery_can_match_an_incoming_ticket()
    {
        var connector = Build(new FakeAutotaskServer(Clock));
        var options = await connector.GetPrioritiesAsync();

        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Printer", ExternalCompanyId = SeededOrganizationId,
            IdempotencyKey = "discovery-priority", Priority = options.First().Value,
        });
        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items.Single();

        options.Select(o => o.SyncValue).Should().Contain(ticket.Priority);
    }

    /// <summary>
    /// A synced ticket has to carry the client's NAME, not just their id.
    ///
    /// An Autotask ticket reports its company as a bare numeric companyID, and the connector passed
    /// that through without ever resolving it. The portal then named every client company after the
    /// id it could not translate — "Company 176" — and that stub reached the client list, the ticket
    /// lists, client workload analytics and the client-facing reports. It reads as data rather than
    /// as an error, which is why it survived: nothing anywhere said the name was missing.
    /// </summary>
    [Fact]
    public async Task A_synced_ticket_carries_the_client_company_name()
    {
        var connector = Build(new FakeAutotaskServer(Clock));
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Printer", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "company-name",
        });

        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items.Single();

        ticket.CompanyName.Should().Be("Acme Corp",
            "the portal falls back to naming the company after its id when this is null");
        ticket.RequesterExternalId.Should().Be(SeededOrganizationId, "the id is still carried too");
    }

    [Fact]
    public async Task An_unknown_company_leaves_the_name_null_rather_than_guessing()
    {
        // Resolution is best-effort: a company the lookup cannot find must leave the name empty so
        // the caller applies its own fallback, rather than being handed a wrong name.
        var server = new FakeAutotaskServer(Clock);
        var connector = Build(server);
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Orphan", ExternalCompanyId = "9999", IdempotencyKey = "unknown-company",
        });

        var ticket = (await connector.GetTicketsAsync(new TicketFilter())).Items
            .Single(t => t.Title == "Orphan");

        ticket.CompanyName.Should().BeNull();
    }

    /// <summary>
    /// An import filter must actually filter, provider-side.
    ///
    /// Company, queue and resource filters are pushed down as Autotask "in" clauses. The fake
    /// understood only eq and gte and let everything else through, so every one of those clauses
    /// matched every row and a filter test could not fail. A connection restricted to one company
    /// would have imported the lot, and the test suite would have agreed it was fine.
    /// </summary>
    [Fact]
    public async Task An_import_filter_restricted_to_one_company_excludes_the_others()
    {
        var connector = Build(new FakeAutotaskServer(Clock));
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "wanted", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "f-in" });
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "other", ExternalCompanyId = "9999", IdempotencyKey = "f-out" });

        var page = await connector.GetTicketsAsync(
            new TicketFilter { CompanyIds = [SeededOrganizationId] });

        page.Items.Select(t => t.Title).Should().BeEquivalentTo(["wanted"]);
    }

    /// <summary>
    /// A count asks Autotask how many tickets a filter matches without reading them, and sends the
    /// filter the read sends. "Open only" has to be asked for here: the sync leaves it to the
    /// runner, which sees each ticket, and a count sees none.
    /// </summary>
    [Fact]
    public async Task A_count_matches_what_the_same_filter_reads_and_can_count_open_tickets_alone()
    {
        var server = new FakeAutotaskServer(Clock);
        var connector = Build(server);
        var first = await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "one", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "c-1" });
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "two", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "c-2" });
        await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "elsewhere", ExternalCompanyId = "9999", IdempotencyKey = "c-3" });
        server.CompleteTicket(long.Parse(first.ExternalId!));

        (await connector.CountTicketsAsync(new TicketFilter())).Should().Be(3);
        (await connector.CountTicketsAsync(new TicketFilter { CompanyIds = [SeededOrganizationId] })).Should().Be(2);
        (await connector.CountTicketsAsync(new TicketFilter { CompanyIds = [SeededOrganizationId], IncludeClosed = false }))
            .Should().Be(1, "the completed one is not open");
        (await connector.GetTicketsAsync(new TicketFilter { CompanyIds = [SeededOrganizationId] })).Items
            .Should().HaveCount(2, "the read and the count send one filter");
    }

    /// <summary>
    /// Reference lists are fetched once per connector, not once per ticket.
    ///
    /// GetTimeEntriesAsync runs per ticket and resolves technician and work-type NAMES from whole
    /// lists. Unmemoized, a sync run re-fetched all 500 resources and all 500 billing codes for
    /// every ticket carrying time — hundreds of identical requests whose answers cannot change
    /// between two tickets of the same run. It was invisible in every result, because the names
    /// were right either way; only the request count showed it.
    /// </summary>
    [Fact]
    public async Task Reference_lists_are_fetched_once_per_run_not_once_per_ticket()
    {
        var server = new FakeAutotaskServer(Clock);
        var connector = Build(server);
        for (var i = 0; i < 3; i++)
            await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
            { Title = $"t{i}", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = $"memo-{i}" });

        var tickets = (await connector.GetTicketsAsync(new TicketFilter())).Items;
        // Every ticket carries time, so every ticket triggers technician and work-type resolution.
        foreach (var t in tickets) server.SeedTimeEntry(long.Parse(t.ExternalId));
        foreach (var t in tickets)
            (await connector.GetTimeEntriesAsync(t.ExternalId)).Should().NotBeEmpty();

        tickets.Should().HaveCountGreaterThanOrEqualTo(3);
        Count(server, "Resources").Should().BeLessThanOrEqualTo(1, "the resource list cannot change mid-run");
        Count(server, "BillingCodes").Should().BeLessThanOrEqualTo(1, "nor can the billing codes");
        Count(server, "Companies").Should().BeLessThanOrEqualTo(1, "company names are batched per page");
    }

    private static int Count(FakeAutotaskServer server, string entity) =>
        server.RequestCounts.Where(kv => kv.Key.Contains(entity, StringComparison.OrdinalIgnoreCase))
            .Sum(kv => kv.Value);
}
