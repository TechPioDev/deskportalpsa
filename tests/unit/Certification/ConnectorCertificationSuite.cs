using System.Security.Cryptography;
using System.Text;
using Desk.Domain.Enums;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// Provider-agnostic connector certification suite (Integration Plan §"Connector Testing
/// Requirements"). Every connector must pass these; derived classes supply a concrete connector.
/// Behaviours that are platform concerns (e.g. create idempotency across retries) are tested at
/// the sync-engine layer, not here, because not every PSA supports them natively.
/// </summary>
public abstract class ConnectorCertificationSuite
{
    protected readonly TestClock Clock = new();

    /// <summary>A healthy connector seeded with at least one organization + contact + technician.</summary>
    protected abstract IServiceManagementConnector CreateConnector();

    /// <summary>A connector rigged so every call fails with the given kind (fault injection).</summary>
    protected abstract IServiceManagementConnector CreateFailingConnector(ConnectorFailureKind kind);

    /// <summary>The external id of a seeded organization the connector knows about.</summary>
    protected abstract string SeededOrganizationId { get; }

    /// <summary>Shared webhook secret used to sign the certification's webhook payloads.</summary>
    protected abstract string WebhookSecret { get; }

    /// <summary>The connector as it is built for a connection that has no webhook secret stored.</summary>
    protected abstract IServiceManagementConnector CreateConnectorWithoutWebhookSecret();

    protected static string Hmac(string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    // ---- capability + connection ----

    [Fact]
    public async Task Declares_capabilities()
    {
        var caps = await CreateConnector().GetCapabilitiesAsync();
        caps.SupportsTicketCreate.Should().BeTrue();
        caps.AuthenticationTypes.Should().NotBeEmpty();
        caps.MaximumPageSize.Should().BePositive();
    }

    [Fact]
    public async Task Test_connection_succeeds()
        => (await CreateConnector().TestConnectionAsync()).Success.Should().BeTrue();

    [Fact]
    public async Task Holidays_come_back_as_iso_days_deduplicated_when_the_provider_has_a_calendar()
    {
        var c = CreateConnector();
        var caps = await c.GetCapabilitiesAsync();
        var items = await c.GetHolidaysAsync();
        if (!caps.SupportsHolidayCalendars)
        {
            items.Should().BeEmpty("a provider without the concept answers empty, not with an exception");
            return;
        }
        items.Should().NotBeEmpty("SupportsHolidayCalendars is a promise, not decoration");
        items.Should().OnlyContain(h => System.Text.RegularExpressions.Regex.IsMatch(h.Date, @"^\d{4}-\d{2}-\d{2}$"),
            "holidays are DAY-scoped — a timestamp invites timezone off-by-one-day bugs");
        items.Should().OnlyHaveUniqueItems(h => h.Date + "|" + h.Name);
    }

    [Fact]
    public async Task Agreements_are_read_per_organization_when_the_provider_supports_contracts()
    {
        var c = CreateConnector();
        var caps = await c.GetCapabilitiesAsync();
        var items = await c.GetAgreementsAsync(SeededOrganizationId);
        if (!caps.SupportsContracts)
        {
            items.Should().BeEmpty("a provider without the concept answers empty, not with an exception");
            return;
        }
        items.Should().NotBeEmpty("SupportsContracts is a promise, not decoration");
        items.Should().OnlyContain(a => a.ExternalId.Length > 0 && a.Name.Length > 0);
        // Type and Status must be the provider's own labels, never raw numeric ids.
        items.Should().OnlyContain(a => a.Type == null || !a.Type.All(char.IsDigit));
        items.Should().OnlyContain(a => a.Status == null || !a.Status.All(char.IsDigit));
    }

    // ---- error mapping ----

    [Fact]
    public async Task Invalid_credentials_surface_as_authentication_failure()
    {
        var act = async () => await CreateFailingConnector(ConnectorFailureKind.Authentication).TestConnectionAsync();
        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.Authentication);
    }

    [Fact]
    public async Task Permission_denied_is_reported()
    {
        var act = async () => await CreateFailingConnector(ConnectorFailureKind.PermissionDenied).GetOrganizationsAsync();
        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.PermissionDenied);
    }

    [Fact]
    public async Task Rate_limit_is_transient_with_retry_after()
    {
        var act = (Func<Task>)(() => CreateFailingConnector(ConnectorFailureKind.RateLimited).GetOrganizationsAsync());
        var ex = (await act.Should().ThrowAsync<ConnectorException>()).Which;
        ex.IsTransient.Should().BeTrue();
        ex.RetryAfter.Should().NotBeNull();
    }

    // ---- directory sync ----

    [Fact]
    public async Task Directory_sync_returns_orgs_contacts_and_technicians()
    {
        var c = CreateConnector();
        (await c.GetOrganizationsAsync()).Should().NotBeEmpty();
        (await c.GetContactsAsync(SeededOrganizationId)).Should().NotBeEmpty();
        (await c.GetTechniciansAsync()).Should().NotBeEmpty();
    }

    // ---- ticket lifecycle ----

    [Fact]
    public async Task Ticket_create_read_update_round_trips()
    {
        var c = CreateConnector();
        var created = await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = "Printer down", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "k1", Priority = "High",
        });
        created.Success.Should().BeTrue();
        created.ExternalId.Should().NotBeNull();

        var read = await c.GetTicketAsync(created.ExternalId!);
        read!.Title.Should().Be("Printer down");

        (await c.UpdateTicketAsync(created.ExternalId!, new UnifiedTicketUpdate { Status = "Resolved", IdempotencyKey = "k2" }))
            .Success.Should().BeTrue();
        (await c.GetTicketAsync(created.ExternalId!))!.Status.Should().Be("Resolved");
    }

    [Fact]
    public async Task Update_of_missing_ticket_is_not_found()
    {
        var act = async () => await CreateConnector().UpdateTicketAsync("999999", new UnifiedTicketUpdate { IdempotencyKey = "x" });
        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.NotFound);
    }

    [Fact]
    public async Task Notes_round_trip_with_their_visibility_flag_intact()
    {
        // GetNotesAsync returns the WHOLE thread — internal notes included, each carrying the
        // provider's own visibility flag. The portal decides per reader who may see what; a
        // connector that pre-filters internal notes hides half the thread from technicians.
        var c = CreateConnector();
        var t = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "T", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "n1" });
        await c.AddPublicNoteAsync(t.ExternalId!, new UnifiedTicketNoteCreateRequest("hello", IsPublic: true, "note-key"));
        await c.AddPublicNoteAsync(t.ExternalId!, new UnifiedTicketNoteCreateRequest("internal analysis", IsPublic: false, "note-key-2"));

        var notes = await c.GetNotesAsync(t.ExternalId!);

        notes.Should().Contain(n => n.Body == "hello" && n.IsPublic);
        notes.Should().Contain(n => n.Body == "internal analysis" && !n.IsPublic);
        // Every connector must attribute a human-written note, so the portal thread never shows a
        // reply bylined with the provider's name instead of its actual author.
        notes.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n.AuthorName));
    }

    [Fact]
    public async Task Attachment_round_trips_with_its_bytes_and_file_name()
    {
        var c = CreateConnector();
        // Providers that cannot serve bytes back declare it; inbound attachment sync is off for them.
        if (!(await c.GetCapabilitiesAsync()).SupportsAttachmentDownload) return;
        var t = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "T", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "att" });
        byte[] content = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

        var created = await c.AddAttachmentAsync(t.ExternalId!,
            new SecureAttachment("report.png", "image/png", content.LongLength, "att/internal-key.png", content));
        created.Success.Should().BeTrue();

        var listed = await c.GetAttachmentsAsync(t.ExternalId!);
        var file = listed.Should().ContainSingle().Subject;
        // The portal's randomized storage key must never surface as the provider-side file name.
        file.FileName.Should().Be("report.png");
        file.SizeBytes.Should().Be(content.Length);

        var payload = await c.DownloadAttachmentAsync(t.ExternalId!, created.ExternalId!);
        payload.Should().NotBeNull();
        payload!.Content.Should().Equal(content); // bytes survive the round trip, not just metadata
    }

    [Fact]
    public async Task Attachment_sweep_finds_a_file_without_the_ticket_being_touched()
    {
        var c = CreateConnector();
        // Providers with no dated tenant-wide query declare it; sync reads their files per ticket.
        if (!(await c.GetCapabilitiesAsync()).SupportsAttachmentSweep) return;
        var t = await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "T", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "sweep" });
        byte[] content = [1, 2, 3];
        await c.AddAttachmentAsync(t.ExternalId!, new SecureAttachment("a.bin", "application/octet-stream", 3, "k", content));

        var swept = (await c.GetRecentAttachmentsAsync(null)).Items;

        // Providers do not reliably bump a ticket's modified date when a file is attached, so sync
        // depends on this dated sweep rather than on the ticket page.
        swept.Should().Contain(r => r.TicketExternalId == t.ExternalId && r.Attachment.FileName == "a.bin");
    }

    // ---- field discovery ----

    [Fact]
    public async Task Field_options_are_retrieved_live()
    {
        var c = CreateConnector();
        (await c.GetStatusesAsync()).Should().NotBeEmpty();
        (await c.GetPrioritiesAsync()).Should().NotBeEmpty();
        (await c.GetQueuesOrBoardsAsync()).Should().NotBeEmpty();
    }

    // ---- incremental read ----

    [Fact]
    public async Task Reading_continues_past_the_first_page_until_every_ticket_is_seen()
    {
        // The contract every connector has to honour, and none of them did: a read returns ONE page
        // plus a way to ask for the next. All three reported "no more pages" unconditionally, so the
        // sync stopped at its page size and reported success — no error, no warning, and the
        // shortfall grew as the desk did. It surfaced as a single ticket, the 101st, whose stale
        // values survived two forced full re-syncs.
        var c = CreateConnector();
        for (var i = 0; i < 5; i++)
            await c.CreateTicketAsync(new UnifiedTicketCreateRequest
            {
                Title = $"t{i}", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = $"page-{i}",
            });

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await c.GetTicketsAsync(new TicketFilter { PageSize = 2, Cursor = cursor });
            seen.AddRange(page.Items.Select(t => t.ExternalId));
            cursor = page.NextCursor;
            if (!page.HasMore) break;
        } while (cursor is not null && ++pages < 20);

        seen.Should().HaveCountGreaterThanOrEqualTo(5, "every created ticket is reachable by paging")
            .And.OnlyHaveUniqueItems("a page boundary must not repeat a ticket");
        pages.Should().BeGreaterThan(0, "5 tickets at 2 per page cannot arrive in one page");
    }

    [Fact]
    public async Task A_first_page_that_is_not_full_reports_no_more_pages()
    {
        // The other half: paging must also STOP. A connector that always claims another page turns
        // every sync into a walk to the safety cap.
        var c = CreateConnector();
        await c.CreateTicketAsync(new UnifiedTicketCreateRequest
        { Title = "only", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "single" });

        var page = await c.GetTicketsAsync(new TicketFilter { PageSize = 50 });

        page.HasMore.Should().BeFalse();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Incremental_read_filters_by_modified_since()
    {
        var c = CreateConnector();
        await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "old", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "old" });
        Clock.Advance(TimeSpan.FromHours(1));
        var cutoff = Clock.GetUtcNow();
        Clock.Advance(TimeSpan.FromMinutes(1));
        await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "new", ExternalCompanyId = SeededOrganizationId, IdempotencyKey = "new" });

        var page = await c.GetTicketsAsync(new TicketFilter { ModifiedSince = cutoff });
        page.Items.Should().OnlyContain(t => t.Title == "new");
    }

    // ---- webhook validation ----

    private WebhookRequest SignedWebhook(string body, DateTimeOffset? ts = null, string? secret = null)
    {
        var timestamp = (ts ?? Clock.GetUtcNow()).ToString("o");
        return new(
            Headers: new Dictionary<string, string> { [WebhookSignature.TimestampHeader] = timestamp },
            Body: body,
            RawSignature: WebhookSignature.Compute(timestamp, body, secret ?? WebhookSecret),
            ReceivedAt: Clock.GetUtcNow());
    }

    [Fact]
    public async Task Valid_webhook_signature_passes()
        => (await CreateConnector().ValidateWebhookAsync(SignedWebhook("{\"eventType\":\"ticket.updated\"}"))).IsValid.Should().BeTrue();

    [Fact]
    public async Task Tampered_signature_is_rejected()
    {
        var req = SignedWebhook("{\"x\":1}") with { RawSignature = "deadbeef" };
        (await CreateConnector().ValidateWebhookAsync(req)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected_replay_protection()
    {
        var stale = Clock.GetUtcNow() - TimeSpan.FromMinutes(30);
        (await CreateConnector().ValidateWebhookAsync(SignedWebhook("{\"x\":1}", stale))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_captured_delivery_cannot_be_replayed_under_a_new_timestamp()
    {
        // The timestamp is part of what is signed. When it was only checked, a delivery captured
        // an hour ago could be sent again with the header changed to "now".
        var captured = SignedWebhook("{\"x\":1}", Clock.GetUtcNow() - TimeSpan.FromMinutes(30));
        var replayed = captured with
        {
            Headers = new Dictionary<string, string> { [WebhookSignature.TimestampHeader] = Clock.GetUtcNow().ToString("o") },
        };

        (await CreateConnector().ValidateWebhookAsync(replayed)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_webhook_secret_stored_no_delivery_is_valid()
    {
        // A connection is given a webhook secret only if someone sets one. Until then the key was
        // the empty string, and a signature made with the empty string is one anybody can make.
        var timestamp = Clock.GetUtcNow().ToString("o");
        const string body = "{\"eventType\":\"ticket.updated\"}";
        WebhookRequest Signed(string signature) => new(
            new Dictionary<string, string> { [WebhookSignature.TimestampHeader] = timestamp }, body, signature, Clock.GetUtcNow());
        var c = CreateConnectorWithoutWebhookSecret();

        (await c.ValidateWebhookAsync(Signed(WebhookSignature.Compute(timestamp, body, "")))).IsValid.Should().BeFalse();
        // Over the body alone: the form that used to be accepted.
        (await c.ValidateWebhookAsync(Signed(Hmac(body, "")))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_delivery_signed_with_another_secret_is_rejected()
        => (await CreateConnector().ValidateWebhookAsync(SignedWebhook("{\"x\":1}", secret: "not-the-secret"))).IsValid.Should().BeFalse();

    [Fact]
    public async Task Webhook_payload_normalizes_to_a_provider_event()
    {
        var body = "{\"eventType\":\"ticket.updated\",\"ticketId\":\"1001\",\"id\":\"evt-9\"}";
        var evt = await CreateConnector().ProcessWebhookAsync(
            new WebhookRequest(new Dictionary<string, string>(), body, null, Clock.GetUtcNow()));
        evt.EventType.Should().Be("ticket.updated");
        evt.ExternalTicketId.Should().Be("1001");
        evt.IdempotencyKey.Should().Be("evt-9");
    }
}
