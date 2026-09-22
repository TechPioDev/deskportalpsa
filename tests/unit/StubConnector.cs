using Desk.Domain.Enums;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;

namespace Desk.Tests.Unit;

/// <summary>
/// Minimal hand-driven connector for sync tests: the caller supplies exactly the tickets and notes
/// the provider should return, and every other contract member is deliberately unsupported so a test
/// that reaches beyond what it declared fails loudly instead of silently exercising mock behaviour.
/// </summary>
public sealed class StubConnector(ProviderType provider = ProviderType.AutotaskPsa) : IServiceManagementConnector
{
    public List<UnifiedTicket> Tickets { get; } = [];

    /// <summary>Public notes per external ticket id, as the provider would return them.</summary>
    public Dictionary<string, List<UnifiedTicketNote>> Notes { get; } = [];

    /// <summary>How many times the runner asked for notes — proves per-ticket call behaviour.</summary>
    public int NoteReads { get; private set; }

    /// <summary>When set, note reads fail with it, to prove one bad ticket cannot fail the run.</summary>
    public ConnectorException? NoteReadFailure { get; set; }

    /// <summary>Attachments per external ticket id, with their bytes.</summary>
    public Dictionary<string, List<(UnifiedAttachment Meta, byte[] Content)>> Attachments { get; } = [];

    /// <summary>Attachments pushed out by the portal, in call order.</summary>
    public List<(string TicketId, SecureAttachment Attachment)> Uploaded { get; } = [];

    /// <summary>When set, attachment reads fail — an unknown list, which must not be read as empty.</summary>
    public ConnectorException? AttachmentReadFailure { get; set; }

    public Task<IReadOnlyList<UnifiedAttachment>> GetAttachmentsAsync(string ticketId, CancellationToken ct = default)
    {
        if (AttachmentReadFailure is not null) throw AttachmentReadFailure;
        return Task.FromResult<IReadOnlyList<UnifiedAttachment>>(
            Attachments.GetValueOrDefault(ticketId, []).Select(a => a.Meta).ToList());
    }

    public Task<IReadOnlyList<ProviderAttachmentRef>> GetRecentAttachmentsAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        AttachmentSweeps++;
        var refs = Attachments
            .SelectMany(kv => kv.Value.Select(a => new ProviderAttachmentRef(kv.Key, a.Meta)))
            .Where(r => since is null || r.Attachment.CreatedAt is null || r.Attachment.CreatedAt >= since)
            .ToList();
        return Task.FromResult<IReadOnlyList<ProviderAttachmentRef>>(refs);
    }

    /// <summary>How many times the runner swept for attachments.</summary>
    public int AttachmentSweeps { get; private set; }

    public Task<DownloadedAttachment?> DownloadAttachmentAsync(string ticketId, string attachmentId, CancellationToken ct = default)
    {
        var hit = Attachments.GetValueOrDefault(ticketId, []).FirstOrDefault(a => a.Meta.ExternalId == attachmentId);
        return Task.FromResult(hit.Meta is null
            ? null
            : new DownloadedAttachment(hit.Meta.FileName, hit.Meta.ContentType, hit.Content));
    }

    public Task<CreateAttachmentResult> AddAttachmentAsync(string ticketId, SecureAttachment attachment, CancellationToken ct = default)
    {
        Uploaded.Add((ticketId, attachment));
        return Task.FromResult(new CreateAttachmentResult(true, $"ext-{Uploaded.Count}", null));
    }

    public ProviderType Provider => provider;

    public Task<PaginatedResult<UnifiedTicket>> GetTicketsAsync(TicketFilter filter, CancellationToken ct = default)
        => Task.FromResult(new PaginatedResult<UnifiedTicket>(Tickets, null, false));

    public Task<IReadOnlyList<UnifiedTicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default)
    {
        NoteReads++;
        if (NoteReadFailure is not null) throw NoteReadFailure;
        return Task.FromResult<IReadOnlyList<UnifiedTicketNote>>(Notes.GetValueOrDefault(ticketId, []));
    }

    private static Task<T> No<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => throw new NotSupportedException($"{member} is not part of this stub; add it to the test that needs it.");

    /// <summary>Time support is off by default so tests opt in rather than pay for the extra call.</summary>
    public bool SupportsTimeEntries { get; set; }

    /// <summary>On by default: most attachment tests need a provider that can serve bytes back.</summary>
    public bool SupportsAttachmentDownload { get; set; } = true;

    /// <summary>Mirrors a provider that can answer "attachments since X"; off exercises the per-ticket path.</summary>
    public bool SupportsAttachmentSweep { get; set; } = true;

    public bool SupportsContracts { get; set; }

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(new ProviderCapabilities
        {
            SupportsTimeEntries = SupportsTimeEntries,
            SupportsAttachments = true,
            SupportsAttachmentDownload = SupportsAttachmentDownload,
            SupportsAttachmentSweep = SupportsAttachmentSweep,
            SupportsContracts = SupportsContracts,
            SupportsHolidayCalendars = SupportsHolidayCalendars,
        });
    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default) => No<ConnectionTestResult>();
    public Task<IReadOnlyList<ExternalOrganization>> GetOrganizationsAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalOrganization>>();
    public Task<IReadOnlyList<ExternalContact>> GetContactsAsync(string organizationId, CancellationToken ct = default) => No<IReadOnlyList<ExternalContact>>();
    public Task<IReadOnlyList<ExternalTechnician>> GetTechniciansAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalTechnician>>();
    public Task<IReadOnlyList<ExternalTechnicianAssignment>> GetTechnicianAssignmentsAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalTechnicianAssignment>>();
    public Task<IReadOnlyList<ExternalDevice>> GetDevicesAsync(string organizationId, CancellationToken ct = default) => No<IReadOnlyList<ExternalDevice>>();

    /// <summary>Agreements the fake provider holds, keyed by organization id.</summary>
    public Dictionary<string, List<ExternalAgreement>> Agreements { get; } = [];
    public Task<IReadOnlyList<ExternalAgreement>> GetAgreementsAsync(string organizationId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExternalAgreement>>(Agreements.GetValueOrDefault(organizationId, []));

    public Task<TimeEntryReadiness> CheckTimeEntryReadinessAsync(CancellationToken ct = default)
        => Task.FromResult(new TimeEntryReadiness(true, "Ready."));

    public bool SupportsHolidayCalendars { get; set; }
    public List<ExternalHoliday> Holidays { get; } = [];
    public Task<IReadOnlyList<ExternalHoliday>> GetHolidaysAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExternalHoliday>>(Holidays);
    /// <summary>Answers from <see cref="Tickets"/>; an id listed in <see cref="FailingTicketIds"/> throws as a provider error would.</summary>
    public Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default)
        => FailingTicketIds.Contains(ticketId)
            ? throw new ConnectorException(ConnectorFailureKind.ProviderError, "provider unavailable")
            : Task.FromResult(Tickets.FirstOrDefault(t => t.ExternalId == ticketId));
    public HashSet<string> FailingTicketIds { get; } = [];
    /// <summary>What the provider answers to the next create; default is acceptance.</summary>
    public CreateTicketResult NextCreateResult { get; set; } = new(true, "9001", null);

    /// <summary>Every create request the caller built, so a test can assert what was actually sent.</summary>
    public List<UnifiedTicketCreateRequest> CreateRequests { get; } = [];

    public Task<CreateTicketResult> CreateTicketAsync(UnifiedTicketCreateRequest ticket, CancellationToken ct = default)
    {
        CreateRequests.Add(ticket);
        return Task.FromResult(NextCreateResult);
    }
    public Task<UpdateTicketResult> UpdateTicketAsync(string ticketId, UnifiedTicketUpdate update, CancellationToken ct = default) => No<UpdateTicketResult>();
    public Task<CreateNoteResult> AddPublicNoteAsync(string ticketId, UnifiedTicketNoteCreateRequest note, CancellationToken ct = default) => No<CreateNoteResult>();
    /// <summary>Time entries per external ticket id.</summary>
    public Dictionary<string, List<UnifiedTimeEntry>> TimeEntries { get; } = [];

    /// <summary>How many times the runner asked for time — proves it does not ask needlessly.</summary>
    public int TimeReads { get; private set; }

    public Task<IReadOnlyList<UnifiedTimeEntry>> GetTimeEntriesAsync(string ticketId, CancellationToken ct = default)
    {
        TimeReads++;
        return Task.FromResult<IReadOnlyList<UnifiedTimeEntry>>(TimeEntries.GetValueOrDefault(ticketId, []));
    }
    /// <summary>When set, pushing time throws it — how a provider REJECTS a payload it dislikes.</summary>
    public ConnectorException? TimeEntryFailure { get; set; }
    public Task<CreateTimeEntryResult> AddTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, CancellationToken ct = default)
        => TimeEntryFailure is { } ex ? throw ex : No<CreateTimeEntryResult>();
    public Task<UpdateTimeEntryResult> UpdateTimeEntryAsync(string entryId, UnifiedTimeEntryUpdate update, CancellationToken ct = default) => No<UpdateTimeEntryResult>();
    public Task<UpdateTimeEntryResult> DeleteTimeEntryAsync(string entryId, CancellationToken ct = default) => No<UpdateTimeEntryResult>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetStatusesAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetPrioritiesAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetQueuesOrBoardsAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetCategoriesAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetWorkTypesAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldOption>> GetWorkRolesAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldOption>>();
    public Task<IReadOnlyList<ExternalFieldDefinition>> GetCustomFieldsAsync(CancellationToken ct = default) => No<IReadOnlyList<ExternalFieldDefinition>>();
    public Task<WebhookValidationResult> ValidateWebhookAsync(WebhookRequest request, CancellationToken ct = default) => No<WebhookValidationResult>();
    public Task<NormalizedProviderEvent> ProcessWebhookAsync(WebhookRequest request, CancellationToken ct = default) => No<NormalizedProviderEvent>();
}
