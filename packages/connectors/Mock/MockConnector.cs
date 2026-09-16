using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desk.Domain.Enums;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;

namespace Desk.Connectors.Mock;

/// <summary>
/// In-memory reference connector. It implements the full contract with seeded data, idempotent
/// creates, and real HMAC webhook validation — enough to drive the connector certification suite
/// and local development without a live PSA. Real Wave-1 connectors (ConnectWise, Autotask) join
/// this package in phases 4-5 and must pass the same suite.
/// </summary>
public sealed class MockConnector : IServiceManagementConnector
{
    private readonly MockConnectorOptions _options;
    private readonly TimeProvider _clock;
    private int _callCount;

    private readonly List<ExternalOrganization> _orgs = [];
    private readonly List<ExternalContact> _contacts = [];
    private readonly List<ExternalTechnician> _techs = [];
    private readonly ConcurrentDictionary<string, UnifiedTicket> _tickets = new();
    private readonly ConcurrentDictionary<string, List<UnifiedTicketNote>> _notes = new();
    private readonly ConcurrentDictionary<string, List<(UnifiedAttachment Meta, byte[] Content)>> _attachments = new();
    private readonly ConcurrentDictionary<string, string> _idempotency = new(); // key -> externalId
    private int _seq = 1000;

    public MockConnector(MockConnectorOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        Seed();
    }

    public ProviderType Provider => _options.Provider;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderCapabilities
        {
            SupportsTicketCreate = true, SupportsTicketUpdate = true, SupportsTicketDelete = false,
            SupportsPublicNotes = true, SupportsPrivateNotes = true, SupportsNoteEmailRecipients = true,
            SupportsAttachments = true, SupportsAttachmentDownload = true, SupportsAttachmentSweep = true,
            SupportsTimeEntries = true, SupportsAssets = false, SupportsContracts = true,
            SupportsHolidayCalendars = true,
            SupportsSlaData = true, SupportsCustomFields = true, SupportsInboundWebhooks = true,
            SupportsOutboundWebhooks = false, SupportsIncrementalSync = true, SupportsBulkRead = true,
            SupportsBulkWrite = false, SupportsCompanies = true, SupportsContacts = true,
            SupportsTechnicians = true, SupportsTeams = true, SupportsQueues = true,
            MaximumPageSize = 100, MaximumAttachmentSize = 25 * 1024 * 1024,
            RateLimitModel = "fixed-window",
            AuthenticationTypes = [AuthenticationType.ApiKey, AuthenticationType.OAuth2ClientCredentials],
        });

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        Guard();
        return Task.FromResult(new ConnectionTestResult(true, "OK", TimeSpan.FromMilliseconds(12)));
    }

    public Task<IReadOnlyList<ExternalOrganization>> GetOrganizationsAsync(CancellationToken ct = default)
    { Guard(); return Task.FromResult<IReadOnlyList<ExternalOrganization>>(_orgs); }

    public Task<IReadOnlyList<ExternalContact>> GetContactsAsync(string organizationId, CancellationToken ct = default)
    { Guard(); return Task.FromResult<IReadOnlyList<ExternalContact>>(_contacts); }

    public Task<IReadOnlyList<ExternalTechnician>> GetTechniciansAsync(CancellationToken ct = default)
    { Guard(); return Task.FromResult<IReadOnlyList<ExternalTechnician>>(_techs); }

    public Task<IReadOnlyList<ExternalTechnicianAssignment>> GetTechnicianAssignmentsAsync(CancellationToken ct = default)
    {
        Guard();
        var rows = _techs.Select((t, i) => new ExternalTechnicianAssignment(
            t.ExternalId, i == 0 ? "R-1" : "R-2", i == 0 ? "Engineer" : "Help Desk", "Q-1")).ToList();
        return Task.FromResult<IReadOnlyList<ExternalTechnicianAssignment>>(rows);
    }

    public Task<PaginatedResult<UnifiedTicket>> GetTicketsAsync(TicketFilter filter, CancellationToken ct = default)
    {
        Guard();
        var query = _tickets.Values.AsEnumerable();
        if (filter.ModifiedSince is { } since)
            query = query.Where(t => (t.ModifiedAt ?? t.CreatedAt) >= since);
        // Ordered and offset by the cursor, like the real connectors: the mock is one of the three
        // implementations the certification suite runs against, so it has to be able to fail the
        // same contract rather than pass by doing less.
        var ordered = query.OrderBy(t => t.ExternalId).ToList();
        var skip = filter.Cursor is { Length: > 0 } c && int.TryParse(c, out var n) && n > 0 ? n : 0;
        var page = ordered.Skip(skip).Take(filter.PageSize).ToList();
        var hasMore = skip + page.Count < ordered.Count;
        return Task.FromResult(new PaginatedResult<UnifiedTicket>(
            page, hasMore ? (skip + page.Count).ToString() : null, hasMore));
    }

    public Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default)
    { Guard(); return Task.FromResult(_tickets.GetValueOrDefault(ticketId)); }

    public Task<CreateTicketResult> CreateTicketAsync(UnifiedTicketCreateRequest ticket, CancellationToken ct = default)
    {
        Guard();
        // Idempotency: a retried create with the same key returns the original ticket.
        if (_idempotency.TryGetValue(ticket.IdempotencyKey, out var existing))
            return Task.FromResult(new CreateTicketResult(true, existing, null));

        var id = $"T-{Interlocked.Increment(ref _seq)}";
        var now = _clock.GetUtcNow();
        _tickets[id] = new UnifiedTicket
        {
            ExternalId = id, Title = ticket.Title, Description = ticket.Description,
            Status = ticket.Status ?? "New", Priority = ticket.Priority ?? "Medium",
            Category = ticket.Category, QueueOrBoard = ticket.QueueOrBoard,
            RequesterEmail = ticket.RequesterEmail, CreatedAt = now, ModifiedAt = now,
        };
        _idempotency[ticket.IdempotencyKey] = id;
        _notes[id] = [];
        return Task.FromResult(new CreateTicketResult(true, id, null));
    }

    public Task<UpdateTicketResult> UpdateTicketAsync(string ticketId, UnifiedTicketUpdate update, CancellationToken ct = default)
    {
        Guard();
        if (!_tickets.TryGetValue(ticketId, out var t))
            throw new ConnectorException(ConnectorFailureKind.NotFound, $"Ticket {ticketId} not found.");
        _tickets[ticketId] = t with
        {
            Status = update.Status ?? t.Status,
            Priority = update.Priority ?? t.Priority,
            Category = update.Category ?? t.Category,
            QueueOrBoard = update.QueueOrBoard ?? t.QueueOrBoard,
            AssignedTechnicianExternalId = update.AssignedTechnicianExternalId ?? t.AssignedTechnicianExternalId,
            ModifiedAt = _clock.GetUtcNow(),
        };
        return Task.FromResult(new UpdateTicketResult(true, null));
    }

    public Task<IReadOnlyList<UnifiedTicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default)
    {
        Guard();
        var list = _notes.GetValueOrDefault(ticketId, []).ToList(); // all notes; IsPublic marks visibility
        return Task.FromResult<IReadOnlyList<UnifiedTicketNote>>(list);
    }

    /// <summary>The last note request received, so a test can assert what actually reached the
    /// provider — recipients included, which is the part that must never be wrong.</summary>
    public UnifiedTicketNoteCreateRequest? LastNoteRequest { get; private set; }

    /// <summary>Seed a technician, for the PSA-provisioning tests.</summary>
    public void AddTechnician(string externalId, string email, string name, bool isActive = true)
        => _techs.Add(new ExternalTechnician(externalId, email, name, isActive));

    /// <summary>Seed a contact for the company-scoped recipient tests.</summary>
    /// <summary>Sets who a ticket is for, as the PSA would report it on a read.</summary>
    public void SetTicketContact(string ticketId, string? name, string? email)
        => _tickets[ticketId] = _tickets[ticketId] with { RequesterName = name, RequesterEmail = email };

    public void AddContact(string externalId, string email, string name, bool isActive = true)
        => _contacts.Add(new ExternalContact(externalId, email, name, isActive));

    public Task<CreateNoteResult> AddPublicNoteAsync(string ticketId, UnifiedTicketNoteCreateRequest note, CancellationToken ct = default)
    {
        Guard();
        LastNoteRequest = note;
        if (!_tickets.ContainsKey(ticketId))
            throw new ConnectorException(ConnectorFailureKind.NotFound, $"Ticket {ticketId} not found.");
        var id = $"N-{Interlocked.Increment(ref _seq)}";
        _notes.GetOrAdd(ticketId, _ => []).Add(new UnifiedTicketNote(id, "Portal", note.Body, note.IsPublic, _clock.GetUtcNow()));
        return Task.FromResult(new CreateNoteResult(true, id, null));
    }

    public Task<IReadOnlyList<UnifiedAttachment>> GetAttachmentsAsync(string ticketId, CancellationToken ct = default)
    {
        Guard();
        var list = _attachments.GetValueOrDefault(ticketId, []).Select(a => a.Meta).ToList();
        return Task.FromResult<IReadOnlyList<UnifiedAttachment>>(list);
    }

    public Task<IReadOnlyList<ProviderAttachmentRef>> GetRecentAttachmentsAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        Guard();
        var refs = _attachments
            .SelectMany(kv => kv.Value.Select(a => new ProviderAttachmentRef(kv.Key, a.Meta)))
            .Where(r => since is null || r.Attachment.CreatedAt is null || r.Attachment.CreatedAt >= since)
            .ToList();
        return Task.FromResult<IReadOnlyList<ProviderAttachmentRef>>(refs);
    }

    public Task<DownloadedAttachment?> DownloadAttachmentAsync(string ticketId, string attachmentId, CancellationToken ct = default)
    {
        Guard();
        var hit = _attachments.GetValueOrDefault(ticketId, []).FirstOrDefault(a => a.Meta.ExternalId == attachmentId);
        return Task.FromResult(hit.Meta is null
            ? null
            : new DownloadedAttachment(hit.Meta.FileName, hit.Meta.ContentType, hit.Content));
    }

    public Task<CreateAttachmentResult> AddAttachmentAsync(string ticketId, SecureAttachment attachment, CancellationToken ct = default)
    {
        Guard();
        if (!_tickets.ContainsKey(ticketId))
            throw new ConnectorException(ConnectorFailureKind.NotFound, $"Ticket {ticketId} not found.");
        var id = $"A-{Interlocked.Increment(ref _seq)}";
        // Keep the bytes, so a connector round trip proves content survives rather than just metadata.
        var meta = new UnifiedAttachment(id, attachment.FileName, attachment.ContentType, attachment.SizeBytes)
        {
            CreatedAt = _clock.GetUtcNow(),
            AuthorName = "Portal",
        };
        _attachments.GetOrAdd(ticketId, _ => []).Add((meta, attachment.Content));
        return Task.FromResult(new CreateAttachmentResult(true, id, null));
    }

    public Task<IReadOnlyList<ExternalDevice>> GetDevicesAsync(string organizationId, CancellationToken ct = default)
    { Guard(); return Task.FromResult<IReadOnlyList<ExternalDevice>>([new ExternalDevice("D-1", "Mock Workstation", "Workstation", "SN-1", true)]); }

    public Task<TimeEntryReadiness> CheckTimeEntryReadinessAsync(CancellationToken ct = default)
    { Guard(); return Task.FromResult(new TimeEntryReadiness(true, "Ready — the mock provider accepts any time entry.")); }

    public Task<IReadOnlyList<ExternalHoliday>> GetHolidaysAsync(CancellationToken ct = default)
    {
        Guard();
        return Task.FromResult<IReadOnlyList<ExternalHoliday>>(
            [new ExternalHoliday("2026-12-25", "Christmas Day"), new ExternalHoliday("2027-01-01", "New Year's Day")]);
    }

    public Task<IReadOnlyList<ExternalAgreement>> GetAgreementsAsync(string organizationId, CancellationToken ct = default)
    {
        Guard();
        return Task.FromResult<IReadOnlyList<ExternalAgreement>>(
            [new ExternalAgreement("AG-1", "Managed Services", "Managed", "Active", _clock.GetUtcNow().AddMonths(-6), null)]);
    }

    public Task<IReadOnlyList<UnifiedTimeEntry>> GetTimeEntriesAsync(string ticketId, CancellationToken ct = default)
    { Guard(); return Task.FromResult<IReadOnlyList<UnifiedTimeEntry>>([]); }

    public Task<CreateTimeEntryResult> AddTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, CancellationToken ct = default)
    { Guard(); return Task.FromResult(new CreateTimeEntryResult(true, $"TE-{Interlocked.Increment(ref _seq)}", null)); }

    public Task<UpdateTimeEntryResult> UpdateTimeEntryAsync(string entryId, UnifiedTimeEntryUpdate update, CancellationToken ct = default)
    { Guard(); return Task.FromResult(new UpdateTimeEntryResult(true, null)); }

    public Task<UpdateTimeEntryResult> DeleteTimeEntryAsync(string entryId, CancellationToken ct = default)
    { Guard(); return Task.FromResult(new UpdateTimeEntryResult(true, null)); }

    public Task<IReadOnlyList<ExternalFieldOption>> GetStatusesAsync(CancellationToken ct = default) =>
        Options("New", "In Progress", "Waiting Customer", "Resolved", "Closed");

    public Task<IReadOnlyList<ExternalFieldOption>> GetPrioritiesAsync(CancellationToken ct = default) =>
        Options("Low", "Medium", "High", "Critical");

    public Task<IReadOnlyList<ExternalFieldOption>> GetQueuesOrBoardsAsync(CancellationToken ct = default) =>
        Options("Service Desk", "Network", "Escalations");

    public Task<IReadOnlyList<ExternalFieldOption>> GetCategoriesAsync(CancellationToken ct = default) =>
        Options("Hardware", "Software", "Access");
    public Task<IReadOnlyList<ExternalFieldOption>> GetWorkTypesAsync(CancellationToken ct = default) =>
        Options("Remote", "Onsite", "Project");
    public Task<IReadOnlyList<ExternalFieldOption>> GetWorkRolesAsync(CancellationToken ct = default) =>
        Options("Engineer", "Consultant");

    public Task<IReadOnlyList<ExternalFieldDefinition>> GetCustomFieldsAsync(CancellationToken ct = default)
    {
        Guard();
        return Task.FromResult<IReadOnlyList<ExternalFieldDefinition>>(
            [new ExternalFieldDefinition("cf_site", "Site", "string", false)]);
    }

    public Task<WebhookValidationResult> ValidateWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        // Timestamp freshness (replay protection).
        if (!request.Headers.TryGetValue("X-Timestamp", out var tsRaw)
            || !DateTimeOffset.TryParse(tsRaw, out var ts))
            return Task.FromResult(new WebhookValidationResult(false, "Missing or invalid timestamp."));
        if (Math.Abs((request.ReceivedAt - ts).TotalSeconds) > _options.WebhookMaxSkew.TotalSeconds)
            return Task.FromResult(new WebhookValidationResult(false, "Timestamp outside allowed skew."));

        // HMAC signature over the raw body.
        var expected = ComputeHmac(request.Body, _options.WebhookSecret);
        var provided = request.RawSignature ?? "";
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
        return Task.FromResult(ok
            ? new WebhookValidationResult(true, null)
            : new WebhookValidationResult(false, "Signature mismatch."));
    }

    public Task<NormalizedProviderEvent> ProcessWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() ?? "unknown" : "unknown";
        var ticketId = root.TryGetProperty("ticketId", out var ti) ? ti.GetString() : null;
        var eventId = root.TryGetProperty("id", out var idp) ? idp.GetString() : null;
        // Idempotency key derives from a stable provider event id, else the body hash.
        var key = eventId ?? ComputeHmac(request.Body, "idem");
        return Task.FromResult(new NormalizedProviderEvent(eventType, ticketId, key, request.ReceivedAt));
    }

    // ---- helpers ----

    private Task<IReadOnlyList<ExternalFieldOption>> Options(params string[] values)
    {
        Guard();
        return Task.FromResult<IReadOnlyList<ExternalFieldOption>>(
            values.Select(v => new ExternalFieldOption(v, v)).ToList());
    }

    /// <summary>Applies fault injection before any successful call returns.</summary>
    private void Guard()
    {
        var n = Interlocked.Increment(ref _callCount);
        if (_options.FailEveryCallWith is { } kind)
            throw Make(kind);
        if (n <= _options.TransientFailuresBeforeSuccess)
            throw Make(ConnectorFailureKind.Timeout);
    }

    private static ConnectorException Make(ConnectorFailureKind kind) => kind switch
    {
        ConnectorFailureKind.RateLimited => new ConnectorException(kind, "Rate limited") { RetryAfter = TimeSpan.FromMilliseconds(1) },
        _ => new ConnectorException(kind, kind.ToString()),
    };

    private static string ComputeHmac(string body, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexStringLower(mac);
    }

    public static string SignBody(string body, string secret) => ComputeHmac(body, secret);

    private void Seed()
    {
        _orgs.Add(new ExternalOrganization("ORG-1", "Acme Corp", true));
        _contacts.Add(new ExternalContact("C-1", "user@acme.test", "Acme User", true));
        _techs.Add(new ExternalTechnician("R-1", "tech@msp.test", "Tech One", true));
    }
}
