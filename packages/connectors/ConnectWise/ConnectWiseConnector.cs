using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desk.Domain.Enums;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;

namespace Desk.Connectors.ConnectWise;

/// <summary>
/// ConnectWise Manage connector over REST API 3.0. Bound to one connection; credentials injected by
/// the factory (from Vault). Normalizes CW's terminology and nested {id,name} shapes into the same
/// unified models the Autotask connector produces — this is where cross-provider parity is realized.
///
/// Provider notes: CW nests references (status/priority/board/company) as objects; list endpoints
/// return bare JSON arrays; updates are JSON-Patch; "Service Board" maps to the portal's Queue and
/// "Member" to Technician. Public vs internal notes use the internalAnalysisFlag.
/// </summary>
public sealed class ConnectWiseConnector(
    HttpClient http, ConnectWiseConnectorConfig config, TimeProvider clock,
    // A callback rather than an ILogger: this project is a provider-neutral library and has no
    // logging dependency. The caller decides where the observation goes.
    Action<string, string>? observeTicketShape = null,
    // Which board statuses actually carry closedFlag. Separate from the shape callback because it
    // answers a different question: not "what fields exist" but "which statuses close a ticket".
    Action<string>? observeTicketClosure = null)
    : IServiceManagementConnector
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ProviderType Provider => ProviderType.ConnectWisePsa;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderCapabilities
        {
            SupportsTicketCreate = true, SupportsTicketUpdate = true, SupportsTicketDelete = false,
            SupportsPublicNotes = true, SupportsPrivateNotes = true, SupportsNoteEmailRecipients = true,
            SupportsAttachments = true, SupportsAttachmentDownload = true, SupportsAttachmentSweep = false,
            SupportsTimeEntries = true, SupportsAssets = true, SupportsContracts = true,
            SupportsHolidayCalendars = true,
            SupportsSlaData = true, SupportsCustomFields = true, SupportsInboundWebhooks = true,
            SupportsOutboundWebhooks = true, SupportsIncrementalSync = true, SupportsBulkRead = true,
            SupportsBulkWrite = false, SupportsCompanies = true, SupportsContacts = true,
            SupportsTechnicians = true, SupportsTeams = true, SupportsQueues = true,
            MaximumPageSize = 1000, MaximumAttachmentSize = 60 * 1024 * 1024,
            RateLimitModel = "concurrent-request",
            AuthenticationTypes = [AuthenticationType.BasicAuth],
        });

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var start = clock.GetTimestamp();
        await GetListAsync<CwCompany>("company/companies", new() { ["pageSize"] = "1" }, ct);
        return new ConnectionTestResult(true, "OK", clock.GetElapsedTime(start));
    }

    public async Task<IReadOnlyList<ExternalOrganization>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwCompany>("company/companies", new() { ["pageSize"] = "1000" }, ct);
        return items.Select(c => new ExternalOrganization(c.Id.ToString(), c.Name ?? "", !c.DeletedFlag)).ToList();
    }

    public async Task<IReadOnlyList<ExternalContact>> GetContactsAsync(string organizationId, CancellationToken ct = default)
    {
        var items = await GetListAsync<CwContact>("company/contacts",
            new() { ["conditions"] = $"company/id={organizationId}", ["pageSize"] = "1000" }, ct);
        return items.Select(c => new ExternalContact(
            c.Id.ToString(), c.Email ?? "", $"{c.FirstName} {c.LastName}".Trim(), !c.InactiveFlag)).ToList();
    }

    public async Task<IReadOnlyList<ExternalTechnician>> GetTechniciansAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwMember>("system/members", new() { ["pageSize"] = "1000" }, ct);
        return items.Select(m => new ExternalTechnician(
            m.Id.ToString(), m.PrimaryEmail ?? "", $"{m.FirstName} {m.LastName}".Trim(), !m.InactiveFlag)).ToList();
    }

    public async Task<IReadOnlyList<ExternalDevice>> GetDevicesAsync(string organizationId, CancellationToken ct = default)
    {
        // CW calls managed assets "configurations"; they carry a type object and a serial/tag.
        var items = await GetListAsync<CwConfiguration>("company/configurations",
            new() { ["conditions"] = $"company/id={organizationId}", ["pageSize"] = "1000" }, ct);
        return items.Select(c => new ExternalDevice(
            c.Id.ToString(),
            c.Name ?? $"Configuration {c.Id}",
            c.Type?.Name,
            c.SerialNumber ?? c.TagNumber,
            !string.Equals(c.Status?.Name, "Inactive", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// ConnectWise is far less strict than Autotask here: a member owns the entry and the work role
    /// is optional, so readiness is simply whether a member is configured.
    /// </summary>
    public async Task<TimeEntryReadiness> CheckTimeEntryReadinessAsync(CancellationToken ct = default)
    {
        // Reachability is the only precondition worth asserting: unlike Autotask, ConnectWise
        // accepts time from the API user and treats the work role as optional.
        await GetListAsync<CwMember>("system/members", new() { ["pageSize"] = "1" }, ct);
        return new TimeEntryReadiness(true,
            "Ready — ConnectWise accepts time from the API user, and the work role is optional. "
            + "Set a member below to attribute entries to a person instead.");
    }

    public async Task<IReadOnlyList<ExternalHoliday>> GetHolidaysAsync(CancellationToken ct = default)
    {
        const int maxLists = 10; // an MSP keeps one or two; cap the fan-out regardless
        var lists = await GetListAsync<CwRef>("schedule/holidayLists", new() { ["pageSize"] = "100" }, ct);

        var all = new List<ExternalHoliday>();
        foreach (var list in lists.Take(maxLists))
        {
            List<CwHoliday> holidays;
            try { holidays = await GetListAsync<CwHoliday>($"schedule/holidayLists/{list.Id}/holidays", new() { ["pageSize"] = "1000" }, ct); }
            catch (ConnectorException) { continue; } // one unreadable list must not lose the rest
            all.AddRange(holidays
                .Where(h => h.Date is not null)
                .Select(h => new ExternalHoliday(h.Date!.Value.ToString("yyyy-MM-dd"), h.Name ?? "Holiday")));
        }
        return all.DistinctBy(h => (h.Date, h.Name)).OrderBy(h => h.Date).ToList();
    }

    public async Task<IReadOnlyList<ExternalAgreement>> GetAgreementsAsync(string organizationId, CancellationToken ct = default)
    {
        var items = await GetListAsync<CwAgreement>("finance/agreements",
            new() { ["conditions"] = $"company/id={organizationId}", ["pageSize"] = "1000" }, ct);
        return items.Select(a => new ExternalAgreement(
            a.Id.ToString(),
            a.Name ?? $"Agreement {a.Id}",
            a.Type?.Name,
            a.AgreementStatus,
            a.StartDate,
            // CW models open-ended agreements with a flag, not a null date — surface them as open-ended.
            a.NoEndingDateFlag ? null : a.EndDate)).ToList();
    }

    /// <summary>
    /// Board coverage, derived from service board teams: who is on a team, and which board that team
    /// serves. The team stands in for the role — ConnectWise has no per-board role the way Autotask
    /// does, and the team is what actually determines who picks work up from a board.
    ///
    /// Teams are only expanded on the board-scoped route (the bulk /service/teams response omits
    /// both boardId and members), so this costs one request per board. Boards are few and the result
    /// is cached by the discovery layer, but the loop is capped so a pathological tenant cannot turn
    /// one discovery into hundreds of calls.
    /// </summary>
    public async Task<IReadOnlyList<ExternalTechnicianAssignment>> GetTechnicianAssignmentsAsync(CancellationToken ct = default)
    {
        const int maxBoards = 50;
        var boards = await GetListAsync<CwRef>("service/boards", new() { ["pageSize"] = "1000" }, ct);

        var rows = new List<ExternalTechnicianAssignment>();
        foreach (var board in boards.Take(maxBoards))
        {
            List<CwBoardTeam> teams;
            try { teams = await GetListAsync<CwBoardTeam>($"service/boards/{board.Id}/teams", new() { ["pageSize"] = "1000" }, ct); }
            catch (ConnectorException) { continue; } // one unreadable board must not lose the rest

            foreach (var team in teams)
                foreach (var memberId in team.Members ?? [])
                    rows.Add(new ExternalTechnicianAssignment(
                        memberId.ToString(), team.Id.ToString(), team.Name, board.Id.ToString()));
        }
        return rows;
    }

    public async Task<PaginatedResult<UnifiedTicket>> GetTicketsAsync(TicketFilter filter, CancellationToken ct = default)
    {
        // ConnectWise pages by number rather than by cursor, so the cursor carries the next page.
        // Ordered by id: without an explicit order the provider is free to return rows in a
        // different arrangement between requests, and page 2 of a shifting order silently skips
        // tickets while repeating others.
        var page = filter.Cursor is { Length: > 0 } c && int.TryParse(c, out var parsed) && parsed > 1 ? parsed : 1;
        var query = new Dictionary<string, string>
        {
            ["pageSize"] = filter.PageSize.ToString(),
            ["page"] = page.ToString(),
            ["orderBy"] = "id asc",
        };
        var conditions = new List<string>();
        if (filter.ModifiedSince is { } since)
            conditions.Add($"lastUpdated>[{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}]");
        if (filter.ExternalCompanyId is { } company)
            conditions.Add($"company/id={company}");

        // Import filters. CW expresses "in" as an OR group over ids; closed state is closedFlag.
        if (filter.CompanyIds.Count > 0)
            conditions.Add(IdGroup("company/id", filter.CompanyIds));
        if (filter.QueueOrBoardIds.Count > 0)
            conditions.Add(IdGroup("board/id", filter.QueueOrBoardIds));
        if (filter.AssignedResourceIds.Count > 0)
            conditions.Add(IdGroup("owner/id", filter.AssignedResourceIds));
        if (!filter.IncludeClosed)
            conditions.Add("closedFlag=false");
        if (filter.ActiveWithinDays is > 0 and { } days)
            conditions.Add($"lastUpdated>[{clock.GetUtcNow().AddDays(-days):yyyy-MM-ddTHH:mm:ssZ}]");

        if (conditions.Count > 0)
            query["conditions"] = string.Join(" and ", conditions);

        // Read once as raw JSON, note which fields the provider actually sent, then deserialize the
        // SAME element — one request, and the shape is observed rather than assumed. Ticket raise
        // and closure dates arrived null however they were read, and a null is indistinguishable
        // from a ticket that genuinely has no date, so the field names have to be established
        // rather than guessed at.
        var raw = await SendAsync<System.Text.Json.JsonElement>(
            HttpMethod.Get, BuildPath("service/tickets", query), null, ct);
        LogTicketShapeOnce(raw);

        var items = raw.ValueKind == System.Text.Json.JsonValueKind.Array
            ? raw.Deserialize<List<CwTicket>>(JsonOpts) ?? []
            : [];

        // A full page means there is probably another; ConnectWise does not report a total, so the
        // last page costs one extra request that comes back empty. Cheaper than missing tickets.
        var hasMore = items.Count >= filter.PageSize;
        return new PaginatedResult<UnifiedTicket>(
            items.Select(ToUnified).ToList(), hasMore ? (page + 1).ToString() : null, hasMore);
    }

    public async Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var t = await GetOneAsync<CwTicket>($"service/tickets/{ticketId}", ct);
        return t is null ? null : ToUnified(t);
    }

    public async Task<CreateTicketResult> CreateTicketAsync(UnifiedTicketCreateRequest ticket, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["summary"] = Truncate(ticket.Title, 100), // CW summary is capped at 100 chars
            ["initialDescription"] = ticket.Description,
            ["company"] = new { id = long.Parse(ticket.ExternalCompanyId) },
        };
        if (ticket.QueueOrBoard is not null) body["board"] = Ref(ticket.QueueOrBoard);
        // Statuses are BOARD-scoped: "New (not responded)" exists on one board and not another, and
        // CW rejects the whole create over it. Resolve against the target board the same way status
        // updates do. With no board given CW picks its default board — whose statuses we cannot know
        // in advance — so the status is left for CW to default too, rather than sent blind.
        if (ticket.Status is not null && ticket.QueueOrBoard is not null && long.TryParse(ticket.QueueOrBoard, out var boardId))
            body["status"] = await ResolveBoardStatusAsync(boardId, ticket.Status, ct);
        if (ticket.Priority is not null) body["priority"] = Ref(ticket.Priority);
        // CW's classification trio maps to the portal's ticket/issue/sub-issue types.
        if (ticket.TicketType is not null) body["type"] = Ref(ticket.TicketType);
        if (ticket.IssueType is not null) body["subType"] = Ref(ticket.IssueType);
        if (ticket.SubIssueType is not null) body["item"] = Ref(ticket.SubIssueType);

        var created = await SendAsync<CwTicket>(HttpMethod.Post, "service/tickets", body, ct);
        return new CreateTicketResult(true, created!.Id.ToString(), null);
    }

    public async Task<UpdateTicketResult> UpdateTicketAsync(string ticketId, UnifiedTicketUpdate update, CancellationToken ct = default)
    {
        var ticket = await GetOneAsync<CwTicket>($"service/tickets/{ticketId}", ct)
            ?? throw new ConnectorException(ConnectorFailureKind.NotFound, $"Ticket {ticketId} not found.");

        // ConnectWise updates are JSON-Patch operations replacing whole reference objects.
        var ops = new List<object>();
        if (update.Status is not null)
            ops.Add(new { op = "replace", path = "status", value = await ResolveBoardStatusAsync(ticket.Board?.Id, update.Status, ct) });
        if (update.Priority is not null) ops.Add(new { op = "replace", path = "priority", value = Ref(update.Priority) });
        if (update.QueueOrBoard is not null) ops.Add(new { op = "replace", path = "board", value = Ref(update.QueueOrBoard) });
        if (update.AssignedTechnicianExternalId is not null)
            ops.Add(new { op = "replace", path = "owner", value = Ref(update.AssignedTechnicianExternalId) });

        await SendAsync<CwTicket>(HttpMethod.Patch, $"service/tickets/{ticketId}", ops, ct);
        return new UpdateTicketResult(true, null);
    }

    public async Task<IReadOnlyList<UnifiedTicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default)
    {
        var notes = await GetListAsync<CwTicketNote>($"service/tickets/{ticketId}/notes", new() { ["pageSize"] = "1000" }, ct);
        // ALL notes, internal included — visibility is a per-reader decision the portal makes at
        // read time (staff see internal, clients never do), not something to pre-filter here where
        // it silently hides half the thread from technicians.
        return notes
            .Select(n =>
            {
                // Side detection follows ATTRIBUTION, not mere presence: CW has been seen returning
                // an empty member stub (no name) on contact-authored notes, so `member != null`
                // over-claims for the MSP. Whichever side actually supplies the display name wrote
                // the note — a customer's note (their portal or email into CW) then lands on the
                // client side of the thread instead of reading as the MSP's own words.
                var memberName = string.IsNullOrWhiteSpace(n.Member?.Name) ? null : n.Member!.Name;
                var contactName = string.IsNullOrWhiteSpace(n.Contact?.Name) ? null : n.Contact!.Name;
                return new UnifiedTicketNote(
                    // Empty author = provider-generated note; the sync layer treats that as a system note.
                    n.Id.ToString(), memberName ?? contactName ?? "", n.Text ?? "",
                    IsPublic: !n.InternalAnalysisFlag, n.DateCreated ?? clock.GetUtcNow(),
                    FromClient: memberName is null && contactName is not null);
            })
            .ToList();
    }

    public async Task<CreateNoteResult> AddPublicNoteAsync(string ticketId, UnifiedTicketNoteCreateRequest note, CancellationToken ct = default)
    {
        // Recipients only ride on a PUBLIC note. Copying anyone on an internal note would be the
        // one mistake this system must never make, so the flags are pinned off rather than merely
        // left unset by the caller.
        var cc = note.IsPublic
            ? note.EmailCc.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var emailContact = note.IsPublic && note.EmailContact;

        var body = new
        {
            text = note.Body,
            detailDescriptionFlag = true,
            internalAnalysisFlag = !note.IsPublic, // public notes are not flagged internal
            customerUpdatedFlag = note.IsPublic,
            // ConnectWise sends the mail, not the portal. processNotifications gates the whole
            // thing: without it CW stores the addresses and emails nobody, which looks identical to
            // success from here.
            processNotifications = emailContact || cc.Count > 0,
            emailContactFlag = emailContact,
            emailCcFlag = cc.Count > 0,
            emailCc = cc.Count > 0 ? string.Join(",", cc) : null,
        };
        var created = await SendAsync<CwTicketNote>(HttpMethod.Post, $"service/tickets/{ticketId}/notes", body, ct);
        return new CreateNoteResult(true, created!.Id.ToString(), null);
    }

    public async Task<IReadOnlyList<UnifiedAttachment>> GetAttachmentsAsync(string ticketId, CancellationToken ct = default)
    {
        var docs = await GetListAsync<CwDocument>("system/documents",
            new() { ["recordType"] = "Ticket", ["recordId"] = ticketId, ["pageSize"] = "1000" }, ct);
        return docs.Select(ToUnified).ToList();
    }

    /// <summary>
    /// ConnectWise indexes documents by the record they hang off, with no tenant-wide "changed since"
    /// query, so there is nothing to sweep. Inbound files are therefore read per ticket rather than
    /// in one dated pass — returning empty here keeps the runner from claiming a sweep happened.
    /// </summary>
    public Task<IReadOnlyList<ProviderAttachmentRef>> GetRecentAttachmentsAsync(DateTimeOffset? since, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProviderAttachmentRef>>([]);

    public async Task<DownloadedAttachment?> DownloadAttachmentAsync(string ticketId, string attachmentId, CancellationToken ct = default)
    {
        // Content comes from a dedicated endpoint that returns raw bytes, not JSON.
        var (bytes, contentType, fileName) = await GetBytesAsync($"system/documents/{attachmentId}/download", ct);
        if (bytes is null || bytes.Length == 0) return null;

        // The download response names the file inconsistently, so fall back to the document record.
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var doc = await GetOneAsync<CwDocument>($"system/documents/{attachmentId}", ct);
            fileName = doc?.FileName ?? doc?.Title ?? $"attachment-{attachmentId}";
        }
        // The download endpoint labels everything application/octet-stream, so a PNG imported from
        // ConnectWise would never render inline and a PDF would not open in a viewer. Derive the
        // type from the file name and only fall back to the header when it says something specific.
        var resolved = contentType is null or "application/octet-stream"
            ? GuessContentType(fileName)
            : contentType;
        return new DownloadedAttachment(fileName, resolved, bytes);
    }

    public async Task<CreateAttachmentResult> AddAttachmentAsync(string ticketId, SecureAttachment attachment, CancellationToken ct = default)
    {
        // Documents are a multipart upload, not JSON: posting the metadata alone is rejected outright
        // (415, "media type 'application/json' is not supported"), so the bytes travel with it.
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Ticket"), "recordType" },
            { new StringContent(ticketId), "recordId" },
            { new StringContent(attachment.FileName), "title" },
        };
        var file = new ByteArrayContent(attachment.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
        form.Add(file, "file", attachment.FileName);

        var created = await SendContentAsync<CwDocument>(HttpMethod.Post, "system/documents", form, ct);
        return new CreateAttachmentResult(true, created!.Id.ToString(), null);
    }

    private static UnifiedAttachment ToUnified(CwDocument d) =>
        new(d.Id.ToString(),
            d.FileName ?? d.Title ?? $"attachment-{d.Id}",
            // ConnectWise reports a document TYPE ("txt"), not a media type, so derive one from the
            // file name and keep the provider's value out of the Content-Type header entirely.
            GuessContentType(d.FileName ?? d.Title),
            d.Size ?? 0)
        {
            CreatedAt = d.CreatedOnDate,
            AuthorName = d.Owner,
        };

    private static string GuessContentType(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" or ".log" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    public async Task<IReadOnlyList<UnifiedTimeEntry>> GetTimeEntriesAsync(string ticketId, CancellationToken ct = default)
    {
        var items = await GetListAsync<CwTimeEntry>("time/entries",
            new() { ["conditions"] = $"chargeToId={ticketId} and chargeToType=\"ServiceTicket\"", ["pageSize"] = "1000" }, ct);
        return items.Select(e => new UnifiedTimeEntry(
            e.Id.ToString(), e.Member?.Id.ToString() ?? "", e.ActualHours ?? 0m,
            !string.Equals(e.BillableOption, "DoNotBill", StringComparison.OrdinalIgnoreCase),
            e.TimeStart ?? clock.GetUtcNow(), e.Notes)
        {
            // CW already nests the member and work type as {id, name}, so no extra lookup is needed.
            TechnicianName = e.Member?.Name,
            WorkType = e.WorkType?.Name,
            BillableOption = e.BillableOption switch
            {
                "DoNotBill" => BillableOption.DoNotBill,
                "NoCharge" => BillableOption.NoCharge,
                _ => Desk.PsaCore.Models.BillableOption.Billable,
            },
        }).ToList();
    }

    public async Task<CreateTimeEntryResult> AddTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["chargeToId"] = long.Parse(ticketId),
            ["chargeToType"] = "ServiceTicket",
            ["timeStart"] = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["actualHours"] = entry.Hours,
            ["billableOption"] = entry.Billable switch
            {
                BillableOption.DoNotBill => "DoNotBill",
                BillableOption.NoCharge => "NoCharge",
                _ => "Billable",
            },
            ["notes"] = entry.Notes,
        };
        if (entry.WorkType is not null) body["workType"] = Ref(entry.WorkType);
        if (entry.WorkRole is not null) body["workRole"] = Ref(entry.WorkRole);
        if (entry.MemberIdentifier is not null) body["member"] = new { identifier = entry.MemberIdentifier };

        var created = await SendAsync<CwRef>(HttpMethod.Post, "time/entries", body, ct);
        return new CreateTimeEntryResult(true, created!.Id.ToString(), null);
    }

    public async Task<UpdateTimeEntryResult> UpdateTimeEntryAsync(string entryId, UnifiedTimeEntryUpdate update, CancellationToken ct = default)
    {
        var ops = new List<object>();
        if (update.Hours is { } h) ops.Add(new { op = "replace", path = "actualHours", value = h });
        if (update.Notes is not null) ops.Add(new { op = "replace", path = "notes", value = update.Notes });
        if (update.Billable is { } b)
            ops.Add(new { op = "replace", path = "billableOption", value = b switch { BillableOption.DoNotBill => "DoNotBill", BillableOption.NoCharge => "NoCharge", _ => "Billable" } });
        if (ops.Count == 0) return new UpdateTimeEntryResult(true, null);

        await SendAsync<CwRef>(HttpMethod.Patch, $"time/entries/{entryId}", ops, ct);
        return new UpdateTimeEntryResult(true, null);
    }

    public async Task<UpdateTimeEntryResult> DeleteTimeEntryAsync(string entryId, CancellationToken ct = default)
    {
        await SendVoidAsync(HttpMethod.Delete, $"time/entries/{entryId}", null, ct);
        return new UpdateTimeEntryResult(true, null);
    }

    /// <summary>
    /// Board statuses, keyed by NAME rather than by id.
    ///
    /// Unusually, the name is BOTH representations here. Statuses are board-scoped, so the same "New"
    /// carries a different id on every board and an id from one board is simply wrong on the next;
    /// the write path resolves a name against whichever board the ticket is actually on. So the name
    /// is what a ticket arrives carrying AND the safest thing to send back.
    /// </summary>
    public async Task<IReadOnlyList<ExternalFieldOption>> GetStatusesAsync(CancellationToken ct = default)
    {
        // Every board, not just the first. Statuses are board-scoped and a mapping is per-connection,
        // so reading one board offers an administrator a list that silently omits the statuses their
        // other boards use — a ticket then arrives in a state the mapping page never let them map,
        // and the only visible symptom is the provider's raw status showing in the portal.
        // Deduped by name because that is now the key, and boards share names by design.
        var boards = await GetListAsync<CwRef>("service/boards", new() { ["pageSize"] = "1000" }, ct);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in boards)
        {
            var statuses = await GetListAsync<CwRef>(
                $"service/boards/{board.Id}/statuses", new() { ["pageSize"] = "1000" }, ct);
            foreach (var s in statuses)
                if (!string.IsNullOrWhiteSpace(s.Name) && seen.Add(s.Name))
                    names.Add(s.Name);
        }

        return names.Select(n => new ExternalFieldOption(n, n) { SyncValue = n }).ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldOption>> GetPrioritiesAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwRef>("service/priorities", new() { ["pageSize"] = "1000" }, ct);
        // Sent as an id, reported as a name.
        return items.Select(p => new ExternalFieldOption(p.Id.ToString(), p.Name ?? "") { SyncValue = p.Name ?? "" })
            .ToList();
    }

    /// <summary>
    /// Boards: filtered by id, reported by name.
    ///
    /// The id is what the import filter needs — it becomes a query condition on board/id, where a
    /// name produces an invalid query rather than a visible error — and the name is what a synced
    /// ticket carries. Both travel on the option now, so the filter picker and the mapping picker
    /// each get the one they need instead of sharing whichever won.
    /// </summary>
    public async Task<IReadOnlyList<ExternalFieldOption>> GetQueuesOrBoardsAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwRef>("service/boards", new() { ["pageSize"] = "1000" }, ct);
        return items.Select(b => new ExternalFieldOption(b.Id.ToString(), b.Name ?? "") { SyncValue = b.Name ?? "" })
            .ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldOption>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var board = (await GetListAsync<CwRef>("service/boards", new() { ["pageSize"] = "1" }, ct)).FirstOrDefault();
        if (board is null) return [];
        var types = await GetListAsync<CwRef>($"service/boards/{board.Id}/types", new() { ["pageSize"] = "1000" }, ct);
        // Sent as an id, reported as a name.
        return types.Select(t => new ExternalFieldOption(t.Id.ToString(), t.Name ?? "") { SyncValue = t.Name ?? "" })
            .ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldOption>> GetWorkTypesAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwRef>("time/workTypes", new() { ["pageSize"] = "1000" }, ct);
        return items.Select(w => new ExternalFieldOption(w.Id.ToString(), w.Name ?? "")).ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldOption>> GetWorkRolesAsync(CancellationToken ct = default)
    {
        var items = await GetListAsync<CwRef>("time/workRoles", new() { ["pageSize"] = "1000" }, ct);
        return items.Select(w => new ExternalFieldOption(w.Id.ToString(), w.Name ?? "")).ToList();
    }

    public Task<IReadOnlyList<ExternalFieldDefinition>> GetCustomFieldsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExternalFieldDefinition>>([]);

    public Task<WebhookValidationResult> ValidateWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        if (!request.Headers.TryGetValue("X-Timestamp", out var tsRaw) || !DateTimeOffset.TryParse(tsRaw, out var ts))
            return Task.FromResult(new WebhookValidationResult(false, "Missing or invalid timestamp."));
        if (Math.Abs((request.ReceivedAt - ts).TotalSeconds) > config.WebhookMaxSkew.TotalSeconds)
            return Task.FromResult(new WebhookValidationResult(false, "Timestamp outside allowed skew."));

        var expected = Hmac(request.Body, config.WebhookSecret);
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.RawSignature ?? ""));
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
        var key = root.TryGetProperty("id", out var idp) ? idp.GetString() ?? Hmac(request.Body, "idem") : Hmac(request.Body, "idem");
        return Task.FromResult(new NormalizedProviderEvent(eventType, ticketId, key, request.ReceivedAt));
    }

    // ---- HTTP plumbing ----

    /// <summary>A CW reference: numeric value → {id}, otherwise {name}. Mapping supplies ids in production.</summary>
    private static object Ref(string value) =>
        long.TryParse(value, out var id) ? new { id } : new { name = value };

    private static readonly string[] ClosedFamily = ["closed", "resolved", "completed", "done", "finished"];

    /// <summary>CW has no IN operator, so an id list becomes an OR group: (f=1 or f=2).</summary>
    private static string IdGroup(string field, IReadOnlyList<string> ids)
        => "(" + string.Join(" or ", ids.Select(i => $"{field}={i}")) + ")";

    /// <summary>
    /// Statuses in ConnectWise are BOARD-scoped: each service board defines its own set, so a status
    /// name (or an id from another board) is invalid on this ticket's board. Resolve the desired
    /// value against the ticket's own board with normalized matching (exact → prefix → contains) so
    /// portal-neutral values like IN_PROGRESS find "In Progress (plan of action)". Fails with the
    /// board's actual options so the caller can correct the mapping.
    /// </summary>
    private async Task<object> ResolveBoardStatusAsync(long? boardId, string desired, CancellationToken ct)
    {
        if (boardId is null) return Ref(desired); // no board on the ticket — let CW validate

        var statuses = await GetListAsync<CwRef>($"service/boards/{boardId}/statuses", new() { ["pageSize"] = "1000" }, ct);
        if (long.TryParse(desired, out var id))
        {
            if (statuses.Any(s => s.Id == id)) return new { id };
            // An id from a different board — fall through to name matching below is pointless; report clearly.
            throw new ConnectorException(ConnectorFailureKind.InvalidRequest,
                $"Status id {id} does not exist on this ticket's board. Available: {string.Join(", ", statuses.Select(s => s.Name))}.");
        }

        static string Norm(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var want = Norm(desired);
        var match = statuses.FirstOrDefault(s => Norm(s.Name ?? "") == want)
                 ?? statuses.FirstOrDefault(s => Norm(s.Name ?? "").StartsWith(want))
                 ?? statuses.FirstOrDefault(s => Norm(s.Name ?? "").Contains(want))
                 // The reverse direction: a verbose mapped value ("New (not responded)") against a
                 // board whose name is the terse prefix ("New"). Guarded to 3+ chars so a
                 // one-letter status cannot swallow everything.
                 ?? statuses.FirstOrDefault(s => Norm(s.Name ?? "") is { Length: >= 3 } n && want.StartsWith(n));

        // Boards name their terminal state differently ("Completed", "Closed (resolved)", "Done").
        // For closed-family requests, fall back to closed-family synonyms before giving up.
        if (match is null && ClosedFamily.Contains(want))
            match = statuses.FirstOrDefault(s => ClosedFamily.Any(syn => Norm(s.Name ?? "").StartsWith(syn)));

        if (match is null)
            throw new ConnectorException(ConnectorFailureKind.InvalidRequest,
                $"No status matching '{desired}' on this ticket's board. Available: {string.Join(", ", statuses.Select(s => s.Name))}.");
        return new { id = match.Id };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool _shapeLogged;

    /// <summary>
    /// Names of the fields ConnectWise actually returns on a ticket, logged once per process at
    /// Information. FIELD NAMES ONLY — never values, so no customer data reaches the log. Kept
    /// permanently rather than as a throwaway: when a provider silently stops sending a field, the
    /// symptom is a null that looks exactly like "this ticket has no date", and this is the only
    /// place that difference is visible.
    ///
    /// Unioned across the whole page, not read off the first ticket. ConnectWise omits null fields
    /// entirely, so an open ticket carries no closure date and one sampled row cannot tell "this
    /// provider never sends the field" from "this particular ticket has no value" — the two
    /// conclusions point at opposite fixes.
    /// </summary>
    private void LogTicketShapeOnce(System.Text.Json.JsonElement raw)
    {
        if (_shapeLogged || observeTicketShape is null) return;
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Array || raw.GetArrayLength() == 0) return;
        _shapeLogged = true;
        try
        {
            var fields = new SortedSet<string>(StringComparer.Ordinal);
            var infoFields = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var t in raw.EnumerateArray())
            {
                if (t.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var p in t.EnumerateObject()) fields.Add(p.Name);
                if (t.TryGetProperty("_info", out var info)
                    && info.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var p in info.EnumerateObject()) infoFields.Add(p.Name);
            }

            // ConnectWise sets closedFlag on a ticket only when its BOARD STATUS is configured as a
            // closing status, so this answers a question about board configuration using data the
            // sync already has: a status that never appears with closedFlag=true is not a closing
            // status, which is why its tickets carry no closure date however finished they look.
            // Status names and a count — no customer data.
            var closure = new SortedDictionary<string, (int Closed, int Open)>(StringComparer.Ordinal);
            foreach (var t in raw.EnumerateArray())
            {
                if (t.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = t.TryGetProperty("status", out var st)
                    && st.ValueKind == System.Text.Json.JsonValueKind.Object
                    && st.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                var closed = t.TryGetProperty("closedFlag", out var cf)
                    && cf.ValueKind == System.Text.Json.JsonValueKind.True;
                var cur = closure.GetValueOrDefault(name);
                closure[name] = closed ? (cur.Closed + 1, cur.Open) : (cur.Closed, cur.Open + 1);
            }

            observeTicketClosure?.Invoke(string.Join(", ",
                closure.Select(kv => $"{kv.Key}: closedFlag true={kv.Value.Closed} false={kv.Value.Open}")));

            observeTicketShape(
                $"[{raw.GetArrayLength()} tickets] {string.Join(", ", fields)}",
                infoFields.Count > 0 ? string.Join(", ", infoFields) : "(none)");
        }
        catch (Exception)
        {
            // Observing the shape must never break the sync that produced it.
        }
    }

    private async Task<List<T>> GetListAsync<T>(string path, Dictionary<string, string> query, CancellationToken ct)
        => await SendAsync<List<T>>(HttpMethod.Get, BuildPath(path, query), null, ct) ?? [];

    private async Task<T?> GetOneAsync<T>(string path, CancellationToken ct) where T : class
    {
        try { return await SendAsync<T>(HttpMethod.Get, path, null, ct); }
        catch (ConnectorException ex) when (ex.Kind == ConnectorFailureKind.NotFound) { return null; }
    }

    private static string BuildPath(string path, Dictionary<string, string> query)
    {
        if (query.Count == 0) return path;
        var qs = string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"{path}?{qs}";
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{config.Credentials.CompanyId}+{config.Credentials.PublicKey}:{config.Credentials.PrivateKey}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Add("clientId", config.Credentials.ClientId);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request failed.", ex);
        }

        if (!resp.IsSuccessStatusCode)
            throw MapError(resp, await SafeBodyAsync(resp, ct));

        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>Sends prepared content (e.g. multipart) rather than a JSON-serialized body.</summary>
    private async Task<T?> SendContentAsync<T>(HttpMethod method, string path, HttpContent content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        Authorize(req);
        req.Content = content;

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request timed out."); }
        catch (HttpRequestException ex)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request failed.", ex); }

        if (!resp.IsSuccessStatusCode) throw MapError(resp, await SafeBodyAsync(resp, ct));
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    /// <summary>Reads a raw (non-JSON) response, used for document downloads.</summary>
    private async Task<(byte[]? Bytes, string? ContentType, string? FileName)> GetBytesAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(req);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request timed out."); }
        catch (HttpRequestException ex)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request failed.", ex); }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return (null, null, null);
            if (!resp.IsSuccessStatusCode) throw MapError(resp, await SafeBodyAsync(resp, ct));
            return (await resp.Content.ReadAsByteArrayAsync(ct),
                    resp.Content.Headers.ContentType?.MediaType,
                    resp.Content.Headers.ContentDisposition?.FileNameStar
                        ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
    }

    private void Authorize(HttpRequestMessage req)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{config.Credentials.CompanyId}+{config.Credentials.PublicKey}:{config.Credentials.PrivateKey}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Add("clientId", config.Credentials.ClientId);
    }

    /// <summary>Send a request that returns no body (e.g. DELETE → 204). Same auth/error handling.</summary>
    private async Task SendVoidAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{config.Credentials.CompanyId}+{config.Credentials.PublicKey}:{config.Credentials.PrivateKey}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Add("clientId", config.Credentials.ClientId);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request timed out."); }
        catch (HttpRequestException ex)
        { throw new ConnectorException(ConnectorFailureKind.Timeout, "ConnectWise request failed.", ex); }

        if (!resp.IsSuccessStatusCode)
            throw MapError(resp, await SafeBodyAsync(resp, ct));
    }

    /// <summary>
    /// Pulls the human-readable reason out of a CW error body ({code, message, errors:[{message}]}).
    /// "ConnectWise rejected the request (400)" tells an admin nothing; "Service Status X not found
    /// for Service Board 8" tells them exactly what to fix.
    /// </summary>
    private static async Task<string?> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var parts = new List<string>();
                if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg)
                    parts.Add(msg);
                if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                    foreach (var e in errs.EnumerateArray())
                        if (e.TryGetProperty("message", out var em) && em.GetString() is { Length: > 0 } detail)
                            parts.Add(detail);
                if (parts.Count > 0) return string.Join(" ", parts);
            }
            catch (JsonException) { /* not JSON — fall through to the raw text */ }
            // Anything the provider said beats a bare status code. ConnectWise answers some
            // rejections (bad clientId, wrong instance for these keys) with plain text or an
            // undocumented shape, and discarding those left admins with nothing to act on.
            var trimmed = raw.Trim();
            return trimmed.Length > 400 ? trimmed[..400] : trimmed;
        }
        catch (Exception) { return null; } // an unreadable error body must not mask the real failure
    }

    private static ConnectorException MapError(HttpResponseMessage resp, string? detail = null) => resp.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(ConnectorFailureKind.Authentication, "ConnectWise rejected the credentials."),
        HttpStatusCode.Forbidden => new(ConnectorFailureKind.PermissionDenied, "ConnectWise denied permission."),
        HttpStatusCode.NotFound => new(ConnectorFailureKind.NotFound, "ConnectWise entity not found."),
        HttpStatusCode.TooManyRequests => new(ConnectorFailureKind.RateLimited, "ConnectWise rate limit hit.")
        {
            RetryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10),
        },
        >= HttpStatusCode.InternalServerError => new(ConnectorFailureKind.ProviderError,
            detail is null ? $"ConnectWise server error ({(int)resp.StatusCode})." : $"ConnectWise server error ({(int)resp.StatusCode}): {detail}"),
        _ => new(ConnectorFailureKind.InvalidRequest,
            detail is null ? $"ConnectWise rejected the request ({(int)resp.StatusCode})." : $"ConnectWise rejected the request: {detail}"),
    };

    private UnifiedTicket ToUnified(CwTicket t) => new()
    {
        ExternalId = t.Id.ToString(),
        Title = t.Summary ?? "",
        Description = t.InitialDescription,
        Status = t.Status?.Name,
        Priority = t.Priority?.Name,
        Category = t.Type?.Name,
        QueueOrBoard = t.Board?.Name,          // Service Board → portal Queue
        AssignedTechnicianExternalId = t.Owner?.Id.ToString(),
        RequesterExternalId = t.Company?.Id.ToString(),
        CompanyName = t.Company?.Name,
        ModifiedAt = t.LastUpdated ?? t.Info?.LastUpdated,
        // Was never mapped: without it the portal recorded its own import date as the ticket's age.
        CreatedAt = t.RaisedAt,
        ResolvedAt = t.DateResolved,
        ClosedAt = t.ClosedAtAny,
        SlaDueAt = t.SlaTargetAt,
    };

    private static string Hmac(string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
}
