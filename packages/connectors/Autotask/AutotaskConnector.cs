using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desk.Domain.Enums;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;

namespace Desk.Connectors.Autotask;

/// <summary>
/// Datto Autotask PSA connector over the REST API v1.0. Bound to one connection; credentials are
/// injected by the factory (resolved from Vault). Provider-specific request/response handling and
/// HTTP→<see cref="ConnectorException"/> mapping live entirely here — callers stay provider-neutral.
///
/// Provider notes: Autotask statuses/priorities are numeric picklist ids (translated by the
/// platform mapping engine, not this connector); notes use a numeric <c>publish</c> flag, so
/// "public" is <see cref="AutotaskConnectorConfig.PublicPublishValue"/>.
/// </summary>
public sealed class AutotaskConnector(
    HttpClient http, AutotaskConnectorConfig config, TimeProvider clock,
    // A callback rather than an ILogger: this project is a provider-neutral library with no logging
    // dependency, so the caller decides where the observation goes.
    Action<string, string>? observePicklist = null)
    : IServiceManagementConnector
{
    // Autotask expects PascalCase request wrappers (MaxRecords/Filter). JsonContent.Create defaults
    // to camelCase (web defaults), so use explicit general options to preserve the names as written.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.General);

    public ProviderType Provider => ProviderType.AutotaskPsa;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderCapabilities
        {
            SupportsTicketCreate = true, SupportsTicketUpdate = true, SupportsTicketDelete = false,
            // No note email recipients: Autotask's TicketNote entity has no To/Cc fields. A public
            // note is published (publish=1) and Autotask's own workflow rules decide who is mailed,
            // from the ticket's contact. Claiming otherwise would offer a control nothing honours.
            SupportsPublicNotes = true, SupportsPrivateNotes = true, SupportsNoteEmailRecipients = false,
            SupportsAttachments = true, SupportsAttachmentDownload = true, SupportsAttachmentSweep = true,
            SupportsTimeEntries = true, SupportsAssets = true, SupportsContracts = true,
            SupportsHolidayCalendars = true,
            SupportsSlaData = true, SupportsCustomFields = true, SupportsInboundWebhooks = true,
            SupportsOutboundWebhooks = false, SupportsIncrementalSync = true, SupportsBulkRead = true,
            SupportsBulkWrite = false, SupportsCompanies = true, SupportsContacts = true,
            SupportsTechnicians = true, SupportsTeams = true, SupportsQueues = true,
            MaximumPageSize = 500, MaximumAttachmentSize = 6 * 1024 * 1024,
            RateLimitModel = "threshold-per-hour",
            AuthenticationTypes = [AuthenticationType.ApiKey],
        });

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var start = clock.GetTimestamp();
        await QueryAsync<AtCompany>("Companies", [Filter("id", "gte", 0)], 1, ct);
        return new ConnectionTestResult(true, "OK", clock.GetElapsedTime(start));
    }

    public async Task<IReadOnlyList<ExternalOrganization>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        var items = await QueryAsync<AtCompany>("Companies", [Filter("id", "gte", 0)], 500, ct);
        return items.Select(c => new ExternalOrganization(c.Id.ToString(), c.CompanyName ?? "", c.IsActive)).ToList();
    }

    public async Task<IReadOnlyList<ExternalContact>> GetContactsAsync(string organizationId, CancellationToken ct = default)
    {
        var items = await QueryAsync<AtContact>("Contacts", [Filter("companyID", "eq", long.Parse(organizationId))], 500, ct);
        return items.Select(c => new ExternalContact(
            c.Id.ToString(), c.EmailAddress ?? "", $"{c.FirstName} {c.LastName}".Trim(), c.IsActive)).ToList();
    }

    // Memoized for this connector's lifetime, like the ticket field metadata. GetTimeEntriesAsync
    // runs PER TICKET and asks for this whole list every time it meets an entry with a resource, so
    // a sync run was re-fetching the same 500 resources once per ticket — the list does not change
    // between two tickets of the same run. A connector instance is built per operation, so an admin
    // refreshing fields still gets current data.
    private Task<IReadOnlyList<ExternalTechnician>>? _technicians;

    public Task<IReadOnlyList<ExternalTechnician>> GetTechniciansAsync(CancellationToken ct = default)
        => _technicians ??= FetchTechniciansAsync(ct);

    private async Task<IReadOnlyList<ExternalTechnician>> FetchTechniciansAsync(CancellationToken ct)
    {
        var items = await QueryAsync<AtResource>("Resources", [Filter("id", "gte", 0)], 500, ct);
        return items.Select(r => new ExternalTechnician(
            r.Id.ToString(), r.Email ?? "", $"{r.FirstName} {r.LastName}".Trim(), r.IsActive)).ToList();
    }

    public async Task<IReadOnlyList<ExternalTechnicianAssignment>> GetTechnicianAssignmentsAsync(CancellationToken ct = default)
    {
        var links = await QueryAsync<AtResourceRole>("ResourceRoles", [Filter("isActive", "eq", true)], 500, ct);
        if (links.Count == 0) return [];

        var roleNames = new Dictionary<long, string>();
        try
        {
            foreach (var r in await QueryAsync<AtRole>("Roles", [Filter("isActive", "eq", true)], 500, ct))
                roleNames[r.Id] = r.Name ?? r.Id.ToString();
        }
        catch (ConnectorException) { /* ids still work; names are the nicety */ }

        return links.Select(l => new ExternalTechnicianAssignment(
            l.ResourceId.ToString(),
            l.RoleId.ToString(),
            roleNames.GetValueOrDefault(l.RoleId),
            // A row without a queue is department-wide coverage, not coverage of no queue.
            l.QueueId is > 0 ? l.QueueId!.Value.ToString() : null)).ToList();
    }

    public async Task<PaginatedResult<UnifiedTicket>> GetTicketsAsync(TicketFilter filter, CancellationToken ct = default)
    {
        var filters = new List<object>();
        if (filter.ModifiedSince is { } since)
            filters.Add(Filter("lastActivityDate", "gte", since.ToUniversalTime().ToString("o")));
        else
            filters.Add(Filter("id", "gte", 0));
        if (filter.ExternalCompanyId is { } company)
            filters.Add(Filter("companyID", "eq", long.Parse(company)));

        // Import filters. Autotask expresses "in" with the "in" operator over a value list.
        if (filter.CompanyIds.Count > 0)
            filters.Add(Filter("companyID", "in", filter.CompanyIds.Select(long.Parse).ToArray()));
        if (filter.QueueOrBoardIds.Count > 0)
            filters.Add(Filter("queueID", "in", filter.QueueOrBoardIds.Select(long.Parse).ToArray()));
        if (filter.AssignedResourceIds.Count > 0)
            filters.Add(Filter("assignedResourceID", "in", filter.AssignedResourceIds.Select(long.Parse).ToArray()));
        if (filter.ActiveWithinDays is > 0 and { } days)
            filters.Add(Filter("lastActivityDate", "gte", clock.GetUtcNow().AddDays(-days).ToString("o")));

        // Autotask answers a query with the first page and a URL for the next. Following it is the
        // whole of pagination here: without it the import stopped at MaxRecords and reported success,
        // so a desk with more tickets than one page silently never saw the rest — and the shortfall
        // grew as the desk did.
        // POST, not GET. The next-page URL continues a POSTed query and Autotask answers a GET on it
        // with 405 "The requested resource does not support http method 'GET'" — which is how this
        // shipped broken once. The filter goes along again so the continuation asks for the same set.
        var result = filter.Cursor is { Length: > 0 } cursor
            ? await SendAsync<AtQueryResult<AtTicket>>(HttpMethod.Post, NextPageUrl(cursor),
                new { MaxRecords = filter.PageSize, Filter = filters }, ct)
            : await QueryPageAsync<AtTicket>("Tickets", filters, filter.PageSize, ct);

        var items = result?.Items ?? [];
        await PrimeCompanyNamesAsync(items.Select(i => i.CompanyId), ct);
        var mapped = new List<UnifiedTicket>(items.Count);
        foreach (var item in items) mapped.Add(await ToUnifiedAsync(item, ct));

        var next = result?.PageDetails?.NextPageUrl;
        var hasMore = !string.IsNullOrWhiteSpace(next);
        return new PaginatedResult<UnifiedTicket>(mapped, hasMore ? next : null, hasMore);
    }

    public async Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var item = await GetByIdAsync<AtTicket>("Tickets", ticketId, ct);
        return item is null ? null : await ToUnifiedAsync(item, ct);
    }

    public async Task<CreateTicketResult> CreateTicketAsync(UnifiedTicketCreateRequest ticket, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = ticket.Title,
            ["description"] = ticket.Description,
            // Numeric picklists — same label→id resolution the update path needs.
            ["status"] = await IdForAsync("status", ticket.Status, ct),
            ["priority"] = await IdForAsync("priority", ticket.Priority, ct),
            ["queueID"] = await IdForAsync("queueID", ticket.QueueOrBoard, ct),
            ["ticketCategory"] = await IdForAsync("ticketCategory", ticket.Category, ct),
            ["companyID"] = long.Parse(ticket.ExternalCompanyId),
        };
        // Optional classification — Autotask tenants differ on which of these are mandatory, so
        // only send what the connection actually configured.
        if (ticket.TicketType is not null) body["ticketType"] = ticket.TicketType;
        if (ticket.IssueType is not null) body["issueType"] = ticket.IssueType;
        if (ticket.SubIssueType is not null) body["subIssueType"] = ticket.SubIssueType;
        var result = await SendAsync<AtCreateResult>(HttpMethod.Post, "V1.0/Tickets", body, ct);
        return new CreateTicketResult(true, result!.ItemId.ToString(), null);
    }

    public async Task<UpdateTicketResult> UpdateTicketAsync(string ticketId, UnifiedTicketUpdate update, CancellationToken ct = default)
    {
        // Confirm existence so a missing ticket surfaces as NotFound rather than a silent no-op.
        _ = await GetByIdAsync<AtTicket>("Tickets", ticketId, ct)
            ?? throw new ConnectorException(ConnectorFailureKind.NotFound, $"Ticket {ticketId} not found.");

        var body = new Dictionary<string, object?> { ["id"] = long.Parse(ticketId) };
        // Every one of these is a numeric picklist in Autotask — resolve labels to ids first.
        if (update.Status is not null) body["status"] = await IdForAsync("status", update.Status, ct);
        if (update.Priority is not null) body["priority"] = await IdForAsync("priority", update.Priority, ct);
        if (update.Category is not null) body["ticketCategory"] = await IdForAsync("ticketCategory", update.Category, ct);
        if (update.QueueOrBoard is not null) body["queueID"] = await IdForAsync("queueID", update.QueueOrBoard, ct);
        if (update.AssignedTechnicianExternalId is not null)
        {
            body["assignedResourceID"] = update.AssignedTechnicianExternalId;
            // Autotask rejects a resource without a role: "you must assign both a assignedResourceID
            // and assignedResourceRoleID". Callers resolve the role from the technician's own
            // coverage, so the pair always travels together.
            if (update.AssignedTechnicianRoleId is not null)
                body["assignedResourceRoleID"] = update.AssignedTechnicianRoleId;
        }

        await SendAsync<AtCreateResult>(HttpMethod.Patch, "V1.0/Tickets", body, ct);
        return new UpdateTicketResult(true, null);
    }

    public async Task<IReadOnlyList<ExternalHoliday>> GetHolidaysAsync(CancellationToken ct = default)
    {
        // All holiday sets merged: an MSP typically maintains one, and the portal's holiday page
        // is a flat calendar anyway.
        var items = await QueryAsync<AtHoliday>("Holidays", [Filter("id", "gte", 0)], 500, ct);
        return items
            .Where(h => h.HolidayDate is not null)
            .Select(h => new ExternalHoliday(h.HolidayDate!.Value.ToString("yyyy-MM-dd"), h.HolidayName ?? "Holiday"))
            .DistinctBy(h => (h.Date, h.Name))
            .OrderBy(h => h.Date)
            .ToList();
    }

    /// <summary>
    /// Autotask's ticket-time rules, checked before anyone relies on them: an entry needs a
    /// resource that is NOT the API user, and a role that resource actually holds. Both are
    /// invisible in a settings form, so both are reported here by name.
    /// </summary>
    public async Task<TimeEntryReadiness> CheckTimeEntryReadinessAsync(CancellationToken ct = default)
    {
        if (config.DefaultTimeEntryResourceId is not { } resourceId || resourceId <= 0)
            return new TimeEntryReadiness(false, "No time-entry technician is set on this connection.")
            {
                Remedies = ["Choose a Time entry technician below — Autotask will not accept time without one."],
            };

        var resources = await QueryAsync<AtResource>("Resources", [Filter("id", "eq", resourceId)], 1, ct);
        var who = resources.Count > 0 ? $"{resources[0].FirstName} {resources[0].LastName}".Trim() : $"resource {resourceId}";
        if (resources.Count == 0)
            return new TimeEntryReadiness(false, $"Resource {resourceId} no longer exists in Autotask.")
            {
                Remedies = ["Pick a current technician below."],
            };

        List<AtResourceRole> links;
        try
        {
            links = await QueryAsync<AtResourceRole>("ResourceRoles",
                [Filter("resourceID", "eq", resourceId), Filter("isActive", "eq", true)], 50, ct);
        }
        catch (ConnectorException ex)
        {
            return new TimeEntryReadiness(false, $"Could not read {who}'s work roles from Autotask: {ex.Message}")
            {
                Remedies = ["Confirm the API user has permission to read Resources and Resource Roles."],
            };
        }

        var roleNames = new Dictionary<long, string>();
        try
        {
            foreach (var r in await QueryAsync<AtRole>("Roles", [Filter("isActive", "eq", true)], 500, ct))
                roleNames[r.Id] = r.Name ?? r.Id.ToString();
        }
        catch (ConnectorException) { /* ids alone still answer the question */ }

        var held = links.Select(l => l.RoleId).Where(id => id > 0).Distinct().ToList();
        var heldNames = held.Select(id => roleNames.GetValueOrDefault(id, id.ToString())).ToList();

        if (held.Count == 0)
            return new TimeEntryReadiness(false, $"{who} holds no active work role, so Autotask will reject every entry.")
            {
                Remedies =
                [
                    $"In Autotask, give {who} an active work role (Admin → Resources → {who} → Roles).",
                    "Or choose a different technician below — one who already works tickets.",
                ],
            };

        var configured = config.DefaultTimeEntryRoleId;
        if (configured is { } role && role > 0 && !held.Contains(role))
            return new TimeEntryReadiness(false,
                $"{who} does not hold the configured work role, which is the pairing Autotask rejects.")
            {
                Remedies =
                [
                    $"Set Default work role to one of: {string.Join(", ", heldNames)}.",
                    "Or clear it — the portal then uses a role they hold.",
                ],
                AvailableRoles = heldNames,
            };

        var using_ = configured is { } c2 && c2 > 0 ? roleNames.GetValueOrDefault(c2, c2.ToString()) : heldNames[0];
        return new TimeEntryReadiness(true, $"Ready — time will be logged as {who} with role {using_}.")
        {
            AvailableRoles = heldNames,
        };
    }

    public async Task<IReadOnlyList<ExternalAgreement>> GetAgreementsAsync(string organizationId, CancellationToken ct = default)
    {
        var items = await QueryAsync<AtContract>("Contracts",
            [Filter("companyID", "eq", long.Parse(organizationId))], 500, ct);
        if (items.Count == 0) return [];

        // Type and status are numeric picklists; the labels come from the tenant's own Contracts
        // metadata — a hardcoded table would silently drift from what their Autotask actually says.
        var types = (await PicklistAsync("Contracts", "contractType", ct)).ToDictionary(o => o.Value, o => o.Label);
        var statuses = (await PicklistAsync("Contracts", "status", ct)).ToDictionary(o => o.Value, o => o.Label);

        return items.Select(c => new ExternalAgreement(
            c.Id.ToString(),
            c.ContractName ?? $"Contract {c.Id}",
            c.ContractType is { } t ? types.GetValueOrDefault(t.ToString(), $"Type {t}") : null,
            c.Status is { } s ? statuses.GetValueOrDefault(s.ToString(), $"Status {s}") : null,
            c.StartDate, c.EndDate)).ToList();
    }

    public async Task<IReadOnlyList<UnifiedTicketNote>> GetNotesAsync(string ticketId, CancellationToken ct = default)
    {
        // ALL notes — internal ones carry IsPublic=false and the portal decides who may read them.
        var items = await QueryAsync<AtTicketNote>("TicketNotes",
            [Filter("ticketID", "eq", long.Parse(ticketId))], 500, ct);

        // Resolve the real author. Autotask puts only an id on the note — a resource (technician) or,
        // when a customer contact wrote it, a contact — so translate both to display names. A thread
        // that attributes every reply to "Autotask" is useless to a reader. Each lookup is a single
        // extra request, made only when a note in this batch actually needs it.
        var names = new Dictionary<long, string>();
        if (items.Any(n => n.CreatorResourceId is > 0))
            await SafeFillAsync(names, async () =>
                (await GetTechniciansAsync(ct)).Select(r => (r.ExternalId, r.DisplayName)));

        var contactNames = new Dictionary<long, string>();
        var contactIds = items.Where(n => n.CreatedByContactId is > 0).Select(n => n.CreatedByContactId!.Value).Distinct().ToList();
        if (contactIds.Count > 0)
            await SafeFillAsync(contactNames, async () =>
                (await QueryAsync<AtContact>("Contacts", [Filter("id", "in", contactIds.ToArray())], 500, ct))
                    .Select(c => (c.Id.ToString(), $"{c.FirstName} {c.LastName}".Trim())));

        return items.Select(n => new UnifiedTicketNote(
            n.Id.ToString(),
            AuthorOf(n, names, contactNames),
            n.Description ?? "", IsPublic: n.Publish == config.PublicPublishValue, n.CreateDateTime ?? clock.GetUtcNow(),
            // createdByContactID set = the CUSTOMER's contact wrote it — client side of the thread.
            FromClient: n.CreatedByContactId is > 0,
            // The resource behind the note, kept as an id. A contact's note has no resource author
            // (the contact wins in AuthorOf too), so it carries none.
            AuthorExternalId: n.CreatedByContactId is > 0 || n.CreatorResourceId is not > 0
                ? null
                : n.CreatorResourceId.ToString())).ToList();
    }

    private static string AuthorOf(AtTicketNote note, IReadOnlyDictionary<long, string> resources, IReadOnlyDictionary<long, string> contacts)
    {
        if (note.CreatedByContactId is { } cid && contacts.TryGetValue(cid, out var contact) && !string.IsNullOrWhiteSpace(contact))
            return contact;
        if (note.CreatorResourceId is { } rid && resources.TryGetValue(rid, out var resource) && !string.IsNullOrWhiteSpace(resource))
            return resource;
        // No resolvable author: a workflow rule, an SLA event, or a deleted resource. An empty author
        // is the contract's marker for a system-generated note — the sync layer filters on it.
        return "";
    }

    /// <summary>Best-effort name lookup: an author-name lookup must never fail the note read itself.</summary>
    private static async Task SafeFillAsync(Dictionary<long, string> target, Func<Task<IEnumerable<(string Id, string Name)>>> load)
    {
        try
        {
            foreach (var (id, name) in await load())
                if (long.TryParse(id, out var parsed)) target[parsed] = name;
        }
        catch (ConnectorException) { /* callers fall back to the neutral provider label */ }
    }

    public async Task<CreateNoteResult> AddPublicNoteAsync(string ticketId, UnifiedTicketNoteCreateRequest note, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["ticketID"] = long.Parse(ticketId),
            // Autotask requires a note title. The unified model has no separate title (portals and
            // most PSAs treat a reply as body-only), so derive a readable one from the first line
            // rather than inventing a tag — nothing is added to the body the customer sees.
            ["title"] = NoteTitle(note.Body),
            ["description"] = note.Body,
            ["noteType"] = 1,
            ["publish"] = note.IsPublic ? config.PublicPublishValue : config.InternalPublishValue,
        };
        // TicketNotes is a CHILD collection in Autotask: creates go to the parent ticket's /Notes
        // route. Posting to the top-level /V1.0/TicketNotes returns 404 ("entity not found").
        // Querying still uses the top-level TicketNotes entity (see GetNotesAsync).
        var result = await SendAsync<AtCreateResult>(HttpMethod.Post, $"V1.0/Tickets/{long.Parse(ticketId)}/Notes", body, ct);
        return new CreateNoteResult(true, result!.ItemId.ToString(), null);
    }

    public async Task<IReadOnlyList<UnifiedAttachment>> GetAttachmentsAsync(string ticketId, CancellationToken ct = default)
    {
        // The list projection never carries the bytes (data is always null here) — content comes
        // from the child route in DownloadAttachmentAsync, one file at a time.
        var items = await QueryAsync<AtTicketAttachment>("TicketAttachments",
            [Filter("parentID", "eq", long.Parse(ticketId))], 500, ct);

        var names = new Dictionary<long, string>();
        if (items.Any(a => a.AttachedByResourceId is > 0))
            await SafeFillAsync(names, async () =>
                (await GetTechniciansAsync(ct)).Select(r => (r.ExternalId, r.DisplayName)));

        return items.Select(a => ToUnified(a, names)).ToList();
    }

    private static UnifiedAttachment ToUnified(AtTicketAttachment a, IReadOnlyDictionary<long, string> resourceNames) =>
        new(a.Id.ToString(),
            a.Title ?? a.FullPath ?? $"attachment-{a.Id}",
            a.ContentType ?? "application/octet-stream",
            (long)(a.FileSize ?? 0))
        {
            CreatedAt = a.AttachDate,
            AuthorName = a.AttachedByResourceId is { } rid && resourceNames.TryGetValue(rid, out var n) ? n : "",
            ExternalNoteId = a.TicketNoteId is > 0 ? a.TicketNoteId!.Value.ToString() : null,
        };

    public async Task<IReadOnlyList<ProviderAttachmentRef>> GetRecentAttachmentsAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        var filters = since is { } from
            ? new List<object> { Filter("attachDate", "gte", from.ToUniversalTime().ToString("o")) }
            : [Filter("id", "gte", 0)];
        var items = await QueryAsync<AtTicketAttachment>("TicketAttachments", filters, 500, ct);
        if (items.Count == 0) return [];

        var names = new Dictionary<long, string>();
        if (items.Any(a => a.AttachedByResourceId is > 0))
            await SafeFillAsync(names, async () =>
                (await GetTechniciansAsync(ct)).Select(r => (r.ExternalId, r.DisplayName)));

        return items.Where(a => a.ParentId is > 0)
            .Select(a => new ProviderAttachmentRef(a.ParentId!.Value.ToString(), ToUnified(a, names)))
            .ToList();
    }

    public async Task<DownloadedAttachment?> DownloadAttachmentAsync(string ticketId, string attachmentId, CancellationToken ct = default)
    {
        // Only the ticket's child route returns the base64 payload; the top-level TicketAttachments
        // entity (query or get-by-id) always answers with data = null.
        var result = await SendAsync<AtQueryResult<AtTicketAttachment>>(
            HttpMethod.Get, $"V1.0/Tickets/{long.Parse(ticketId)}/Attachments/{long.Parse(attachmentId)}", null, ct);
        var item = result?.Items.FirstOrDefault();
        if (item?.Data is not { Length: > 0 } data) return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return null; } // corrupt payload is a miss, not a crash

        return new DownloadedAttachment(
            item.Title ?? item.FullPath ?? $"attachment-{item.Id}",
            item.ContentType ?? "application/octet-stream",
            bytes);
    }

    public async Task<CreateAttachmentResult> AddAttachmentAsync(string ticketId, SecureAttachment attachment, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["parentID"] = long.Parse(ticketId),
            ["title"] = attachment.FileName,
            // fullPath is the file's name as Autotask stores it. The portal's randomized storage key
            // must never leak here — it is an internal object-storage detail, and using it produced
            // downloads named after a GUID.
            ["fullPath"] = attachment.FileName,
            ["contentType"] = attachment.ContentType,
            ["attachmentType"] = "FILE_ATTACHMENT",
            ["publish"] = config.PublicPublishValue,
            ["data"] = Convert.ToBase64String(attachment.Content),
        };
        // File it against the note it was posted with. Autotask accepts the field and then stores
        // null for it — the association appears to be settable only from its own UI — so the portal
        // keeps its own note link and does not depend on reading this back. Sent anyway: it is the
        // documented field, it costs nothing, and other tenants/versions may honour it.
        if (long.TryParse(attachment.ExternalNoteId, out var noteId)) body["ticketNoteID"] = noteId;
        // Attachments are a child collection, same as notes.
        var result = await SendAsync<AtCreateResult>(HttpMethod.Post, $"V1.0/Tickets/{long.Parse(ticketId)}/Attachments", body, ct);
        return new CreateAttachmentResult(true, result!.ItemId.ToString(), null);
    }

    // Autotask installed-products sync isn't wired yet; report none rather than failing the panel.
    public Task<IReadOnlyList<ExternalDevice>> GetDevicesAsync(string organizationId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExternalDevice>>([]);

    public async Task<IReadOnlyList<UnifiedTimeEntry>> GetTimeEntriesAsync(string ticketId, CancellationToken ct = default)
    {
        var items = await QueryAsync<AtTimeEntry>("TimeEntries",
            [Filter("ticketID", "eq", long.Parse(ticketId))], 500, ct);
        if (items.Count == 0) return [];

        // Ids alone are unreadable in a time summary, so resolve technician and work-type names.
        // Each lookup is one request, and only runs when an entry actually references one.
        var techs = new Dictionary<long, string>();
        if (items.Any(e => e.ResourceId is > 0))
            await SafeFillAsync(techs, async () =>
                (await GetTechniciansAsync(ct)).Select(r => (r.ExternalId, r.DisplayName)));

        var workTypes = new Dictionary<long, string>();
        if (items.Any(e => e.BillingCodeId is > 0))
            await SafeFillAsync(workTypes, async () =>
                (await GetWorkTypesAsync(ct)).Select(o => (o.Value, o.Label)));

        return items.Select(e =>
        {
            var billable = e.IsNonBillable is not true;
            return new UnifiedTimeEntry(
                e.Id.ToString(),
                e.ResourceId?.ToString() ?? "",
                e.HoursWorked ?? 0m,
                billable,
                e.DateWorked ?? clock.GetUtcNow(),
                e.SummaryNotes)
            {
                TechnicianName = e.ResourceId is { } rid && techs.TryGetValue(rid, out var tn) ? tn : null,
                WorkType = e.BillingCodeId is { } bid && workTypes.TryGetValue(bid, out var wt) ? wt : null,
                // Parsed all along, and then dropped here — which is how "See Internal Notes"
                // reached the portal with nothing behind it.
                InternalNotes = e.InternalNotes,
                // Autotask has no "no charge" flag on the entry itself: billable time with nothing
                // to bill is the closest equivalent.
                BillableOption = billable
                    ? (e.HoursToBill is 0m ? BillableOption.NoCharge : BillableOption.Billable)
                    : BillableOption.DoNotBill,
            };
        }).ToList();
    }

    public async Task<CreateTimeEntryResult> AddTimeEntryAsync(string ticketId, UnifiedTimeEntryCreateRequest entry, CancellationToken ct = default)
    {
        // Autotask refuses its own API-only user as the time owner, so the connection must nominate
        // a real technician. Say so plainly rather than surfacing the provider's opaque rejection.
        if (!TryResourceId(entry.MemberIdentifier, out var resourceId))
            return new CreateTimeEntryResult(false, null,
                "Autotask needs a technician to own the time entry, and rejects API-only users. " +
                "Set a default time-entry resource on this connection.");

        var roleId = await ResolveRoleIdAsync(entry.WorkRole, resourceId, ct);
        if (roleId is null)
            return new CreateTimeEntryResult(false, null,
                "Autotask requires a work role on ticket time, and this technician has no active role. " +
                "Pick a work role, or set a default on the connection.");

        // Ticket time must carry a start/stop window. The portal captures a duration, so anchor the
        // window to end now and run back by that duration.
        var end = clock.GetUtcNow();
        var start = end.AddHours(-(double)entry.Hours);
        var billable = entry.Billable == BillableOption.Billable;

        var body = new Dictionary<string, object?>
        {
            ["ticketID"] = long.Parse(ticketId),
            ["resourceID"] = resourceId,
            ["roleID"] = roleId,
            ["dateWorked"] = start.ToString("o"),
            ["startDateTime"] = start.ToString("o"),
            ["endDateTime"] = end.ToString("o"),
            ["hoursWorked"] = entry.Hours,
            ["hoursToBill"] = billable ? entry.Hours : 0m,
            ["isNonBillable"] = entry.Billable == BillableOption.DoNotBill,
            // Autotask makes summary notes MANDATORY ("TimeEntry.summaryNotes can not be blank")
            // while ConnectWise does not, so an entry logged without notes was accepted here and
            // rejected there — losing the technician's time over a field they were never asked for.
            // The placeholder states only what is true; the UI asks for real notes up front.
            ["summaryNotes"] = string.IsNullOrWhiteSpace(entry.Notes) ? "Time logged from Desk Portal." : entry.Notes,
        };
        if (long.TryParse(entry.WorkType, out var billingCode)) body["billingCodeID"] = billingCode;

        var result = await SendAsync<AtCreateResult>(HttpMethod.Post, "V1.0/TimeEntries", body, ct);
        return new CreateTimeEntryResult(true, result!.ItemId.ToString(), null);
    }

    public async Task<UpdateTimeEntryResult> UpdateTimeEntryAsync(string entryId, UnifiedTimeEntryUpdate update, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["id"] = long.Parse(entryId) };
        if (update.Hours is { } hours)
        {
            body["hoursWorked"] = hours;
            if (update.Billable is BillableOption.Billable or null) body["hoursToBill"] = hours;
        }
        if (update.Billable is { } billable)
        {
            body["isNonBillable"] = billable == BillableOption.DoNotBill;
            if (billable != BillableOption.Billable) body["hoursToBill"] = 0m;
        }
        if (update.Notes is not null) body["summaryNotes"] = update.Notes;

        await SendAsync<AtCreateResult>(HttpMethod.Patch, "V1.0/TimeEntries", body, ct);
        return new UpdateTimeEntryResult(true, null);
    }

    public async Task<UpdateTimeEntryResult> DeleteTimeEntryAsync(string entryId, CancellationToken ct = default)
    {
        await SendAsync<AtCreateResult>(HttpMethod.Delete, $"V1.0/TimeEntries/{long.Parse(entryId)}", null, ct);
        return new UpdateTimeEntryResult(true, null);
    }

    /// <summary>Time owner: the caller's choice, else the connection's configured default.</summary>
    private bool TryResourceId(string? requested, out long resourceId)
    {
        if (long.TryParse(requested, out resourceId) && resourceId > 0) return true;
        resourceId = config.DefaultTimeEntryResourceId ?? 0;
        return resourceId > 0;
    }

    /// <summary>
    /// Work role for a ticket time entry, which Autotask makes mandatory. Falls back to the
    /// connection default, then to any active role the resource actually holds — making an admin
    /// hand-pick a role id for every entry would make time logging unusable.
    /// </summary>
    private async Task<long?> ResolveRoleIdAsync(string? requested, long resourceId, CancellationToken ct)
    {
        var preferred = long.TryParse(requested, out var explicitRole) && explicitRole > 0 ? explicitRole
            : config.DefaultTimeEntryRoleId is { } configured and > 0 ? configured
            : (long?)null;

        // Autotask does not accept "a technician" and "a role" independently — the PAIR has to exist
        // in ResourceRoles, or it answers HTTP 500 "The specified AssignedResourceID and
        // AssignedRoleID combination is not currently defined". The old code trusted a configured
        // role blindly and only looked roles up when nothing was configured, so an admin who picked
        // a sensible-sounding role the technician does not actually hold got that 500 on every
        // entry, with no way to tell which half was wrong.
        List<long> held;
        try
        {
            held = (await QueryAsync<AtResourceRole>("ResourceRoles",
                    [Filter("resourceID", "eq", resourceId), Filter("isActive", "eq", true)], 50, ct))
                .Select(r => r.RoleId).Where(id => id > 0).Distinct().ToList();
        }
        catch (ConnectorException)
        {
            return preferred; // lookup unavailable — send the request and let Autotask judge
        }

        if (preferred is { } want && held.Contains(want)) return want;
        // Prefer a role this resource genuinely holds over one that is merely configured: a valid
        // pairing logs the time, an invalid one loses it.
        if (held.Count > 0) return held[0];
        if (preferred is not null) return preferred;

        throw new ConnectorException(ConnectorFailureKind.InvalidRequest,
            $"Autotask resource {resourceId} holds no active work role, so it cannot own ticket time. " +
            "Give the technician a role in Autotask, or choose a different time-entry technician on this connection.");
    }

    // Every one of these is a numeric picklist: Autotask is SENT the id and REPORTS the label, so
    // each option carries the id as its value and the label as its sync value. Collapsing the two
    // is what broke mapping on both providers.
    public Task<IReadOnlyList<ExternalFieldOption>> GetStatusesAsync(CancellationToken ct = default) => PicklistAsync("status", ct);
    public Task<IReadOnlyList<ExternalFieldOption>> GetPrioritiesAsync(CancellationToken ct = default) => PicklistAsync("priority", ct);
    public Task<IReadOnlyList<ExternalFieldOption>> GetCategoriesAsync(CancellationToken ct = default) => PicklistAsync("ticketCategory", ct);
    public Task<IReadOnlyList<ExternalFieldOption>> GetQueuesOrBoardsAsync(CancellationToken ct = default) => PicklistAsync("queueID", ct);

    /// <summary>
    /// Work types are billing codes, not a ticket picklist. UseType 1 is the general allocation
    /// code set — the labour codes ("Remote Support", "Onsite Support") technicians bill against.
    /// </summary>
    // Memoized for the same reason as the resource list: GetTimeEntriesAsync asks for every billing
    // code once per ticket that has a work type on an entry.
    private Task<IReadOnlyList<ExternalFieldOption>>? _workTypes;

    public Task<IReadOnlyList<ExternalFieldOption>> GetWorkTypesAsync(CancellationToken ct = default)
        => _workTypes ??= FetchWorkTypesAsync(ct);

    private async Task<IReadOnlyList<ExternalFieldOption>> FetchWorkTypesAsync(CancellationToken ct)
    {
        var items = await QueryAsync<AtBillingCode>("BillingCodes",
            [Filter("useType", "eq", 1), Filter("isActive", "eq", true)], 500, ct);
        return items.Select(b => new ExternalFieldOption(b.Id.ToString(), b.Name ?? b.Id.ToString(), true)).ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldOption>> GetWorkRolesAsync(CancellationToken ct = default)
    {
        var items = await QueryAsync<AtRole>("Roles", [Filter("isActive", "eq", true)], 500, ct);
        return items.Select(r => new ExternalFieldOption(r.Id.ToString(), r.Name ?? r.Id.ToString(), true)).ToList();
    }

    public async Task<IReadOnlyList<ExternalFieldDefinition>> GetCustomFieldsAsync(CancellationToken ct = default)
    {
        var info = await SendAsync<AtFieldInfoResult>(HttpMethod.Get, "V1.0/Tickets/entityInformation/userDefinedFields", null, ct);
        return (info?.Fields ?? []).Select(f => new ExternalFieldDefinition(
            f.Name ?? "", f.Name ?? "", "string", false)).ToList();
    }

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

    private Task<IReadOnlyList<ExternalFieldOption>> PicklistAsync(string fieldName, CancellationToken ct)
        => PicklistAsync("Tickets", fieldName, ct);

    // Ticket field metadata, fetched at most once per connector instance: a sync run maps hundreds
    // of tickets and every one of them needs the same picklists.
    private Task<AtFieldInfoResult?>? _ticketFields;
    private Task<AtFieldInfoResult?> TicketFieldsAsync(CancellationToken ct)
        => _ticketFields ??= SendAsync<AtFieldInfoResult>(HttpMethod.Get, "V1.0/Tickets/entityInformation/fields", null, ct);

    private readonly HashSet<string> _observedPicklists = [];

    /// <summary>
    /// Reports a mapped field's picklist as id=label pairs, once per field per connector.
    ///
    /// These are configuration labels — status, priority, queue and category names — never anything
    /// a customer wrote. Kept because a mapping rule holding a bare id is unreadable without them:
    /// answering "which queue is 29682833?" otherwise needs someone to open the admin UI, and a rule
    /// nobody can read is a rule nobody fixes.
    /// </summary>
    private void ObservePicklistOnce(string field, List<AtPicklistValue> values)
    {
        if (observePicklist is null || values.Count == 0) return;
        if (!_observedPicklists.Add(field)) return;
        try
        {
            observePicklist(field, string.Join(", ", values.Select(v => $"{v.Value}={v.Label}")));
        }
        catch (Exception)
        {
            // Observing the shape must never break the sync that produced it.
        }
    }

    private async Task<List<AtPicklistValue>> TicketPicklistAsync(string field, CancellationToken ct)
    {
        try
        {
            var info = await TicketFieldsAsync(ct);
            var values = info?.Fields.FirstOrDefault(f => string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase))
                ?.PicklistValues ?? [];
            ObservePicklistOnce(field, values);
            return values;
        }
        catch (ConnectorException)
        {
            // Metadata is an enhancement, not a dependency: without it values pass through
            // unresolved, exactly as they did before — never fail a sync over a label lookup.
            return [];
        }
    }

    private static string Norm(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// Picklist id → the tenant's own label, for values coming FROM Autotask. Without this the
    /// portal stored raw ids and showed users a status of "1".
    /// </summary>
    // Company id -> name, for the lifetime of this connector. Autotask reports companies only by
    // id on a ticket, and a sync run sees the same handful of companies across hundreds of tickets.
    private readonly Dictionary<long, string> _companyNames = [];

    /// <summary>
    /// Resolves the names for a page's companies in ONE request, before the page is projected.
    ///
    /// Batched rather than per ticket: a page of 100 tickets spans a few companies, so this is one
    /// extra call per page instead of one per ticket. Ids already known are not asked for again, so
    /// later pages usually cost nothing.
    ///
    /// A failure here leaves names unresolved rather than failing the sync. Names are an
    /// enhancement to a ticket the portal can already store; losing the import to lose a label
    /// would be the worse trade.
    /// </summary>
    private async Task PrimeCompanyNamesAsync(IEnumerable<long> companyIds, CancellationToken ct)
    {
        var wanted = companyIds.Where(id => id > 0 && !_companyNames.ContainsKey(id)).Distinct().ToArray();
        if (wanted.Length == 0) return;

        try
        {
            var companies = await QueryAsync<AtCompany>(
                "Companies", [Filter("id", "in", wanted)], wanted.Length, ct);
            foreach (var c in companies)
                if (!string.IsNullOrWhiteSpace(c.CompanyName))
                    _companyNames[c.Id] = c.CompanyName!;
        }
        catch (ConnectorException)
        {
            // Left unresolved on purpose — see the summary above.
        }
    }

    private async Task<string?> LabelForAsync(string field, string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        var values = await TicketPicklistAsync(field, ct);
        return values.FirstOrDefault(v => v.Value == id)?.Label ?? id;
    }

    /// <summary>
    /// The reverse, for values going TO Autotask: every one of these fields is a numeric picklist,
    /// so sending a label ("In Progress") earns a 500 "Could not convert string to integer". Already
    /// numeric values pass straight through; unmatched labels are sent as-is so Autotask's own
    /// validation — not a guess here — has the final say.
    /// </summary>
    private async Task<object?> IdForAsync(string field, string? value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (long.TryParse(value, out var already)) return already;

        var values = await TicketPicklistAsync(field, ct);
        // No metadata (older tenant, or the call failed) — pass through and let Autotask judge,
        // exactly as this connector behaved before label resolution existed.
        if (values.Count == 0) return value;

        var want = Norm(value);
        var match = values.FirstOrDefault(v => Norm(v.Label ?? "") == want)
                 ?? values.FirstOrDefault(v => Norm(v.Label ?? "").StartsWith(want))
                 ?? values.FirstOrDefault(v => want.Length >= 3 && Norm(v.Label ?? "").Contains(want));
        if (match?.Value is { } m && long.TryParse(m, out var id)) return id;

        // Unmappable. Autotask's own answer here is HTTP 500 "Could not convert string to integer",
        // which tells the reader nothing about how to fix it. Name the real options instead — the
        // fix is a field mapping, and this is the message that says so.
        throw new ConnectorException(ConnectorFailureKind.InvalidRequest,
            $"\"{value}\" is not a valid Autotask {field}. Map it to one of: " +
            $"{string.Join(", ", values.Where(v => v.IsActive).Select(v => v.Label))}.");
    }

    private async Task<IReadOnlyList<ExternalFieldOption>> PicklistAsync(
        string entity, string fieldName, CancellationToken ct)
    {
        var info = await SendAsync<AtFieldInfoResult>(HttpMethod.Get, $"V1.0/{entity}/entityInformation/fields", null, ct);
        var field = info?.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        return (field?.PicklistValues ?? [])
            .Select(p =>
            {
                var label = p.Label ?? p.Value ?? "";
                // ToUnifiedAsync resolves these ids to labels on the way in, so the label is what a
                // ticket arrives carrying and the id is what a write or a filter has to send.
                return new ExternalFieldOption(p.Value ?? "", label, p.IsActive) { SyncValue = label };
            }).ToList();
    }

    /// <summary>
    /// Autotask note titles are mandatory, and its UI prints the title ABOVE the description —
    /// so a title that is 250 characters of the body renders the note as though it were posted
    /// twice, the opening paragraph in bold and again below. Reported as "we sent one note but
    /// Autotask shows two".
    ///
    /// A short heading instead: the first line, cut at a word boundary and elided, so it reads as
    /// a subject line rather than a chopped copy of the paragraph under it. Kept as an excerpt
    /// rather than a constant like "Portal reply" because Autotask's note LIST shows titles, and
    /// the same words on every row would make that list unscannable.
    /// </summary>
    private const int NoteTitleMax = 80;

    private static string NoteTitle(string body)
    {
        var line = (body ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0) ?? "Portal reply";
        if (line.Length <= NoteTitleMax) return line;

        var cut = line[..NoteTitleMax];
        var lastSpace = cut.LastIndexOf(' ');
        // Only honour the word boundary if it leaves a usable heading; a first "word" longer than
        // that (a pasted URL, say) is better hard-cut than reduced to nothing.
        if (lastSpace >= 40) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', ';', ':', '.', '-', '—') + "…";
    }

    private static object Filter(string field, string op, object value) => new { op, field, value };

    private async Task<List<T>> QueryAsync<T>(string entity, List<object> filters, int maxRecords, CancellationToken ct)
        => (await QueryPageAsync<T>(entity, filters, maxRecords, ct))?.Items ?? [];

    /// <summary>The whole page, page details included, for reads that continue past the first one.</summary>
    private Task<AtQueryResult<T>?> QueryPageAsync<T>(string entity, List<object> filters, int maxRecords, CancellationToken ct)
        => SendAsync<AtQueryResult<T>>(HttpMethod.Post, $"V1.0/{entity}/query",
            new { MaxRecords = maxRecords, Filter = filters }, ct);

    /// <summary>
    /// A next-page URL, checked to be on the same host we were configured to talk to.
    ///
    /// The value arrives in a response body, so it is provider-supplied input rather than something
    /// this code chose. Following it unchecked would let whatever answered the first request point
    /// the next one anywhere — the credentials go along with it. Refusing loudly rather than
    /// stopping quietly, because a silent stop is the exact failure pagination was added to end.
    /// </summary>
    private string NextPageUrl(string cursor)
    {
        if (!Uri.TryCreate(cursor, UriKind.Absolute, out var next))
            throw new ConnectorException(ConnectorFailureKind.ProviderError,
                $"Autotask returned a next-page URL that is not a valid absolute URL: '{cursor}'.");

        var configured = new Uri(config.BaseUrl, UriKind.Absolute);
        if (!string.Equals(next.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
            || next.Scheme != configured.Scheme)
            throw new ConnectorException(ConnectorFailureKind.ProviderError,
                $"Autotask returned a next-page URL on a different host ({next.Host}) than the configured "
                + $"endpoint ({configured.Host}); refusing to follow it.");

        return next.ToString();
    }

    private async Task<T?> GetByIdAsync<T>(string entity, string id, CancellationToken ct) where T : class
    {
        try
        {
            var result = await SendAsync<AtItemResult<T>>(HttpMethod.Get, $"V1.0/{entity}/{id}", null, ct);
            return result?.Item;
        }
        catch (ConnectorException ex) when (ex.Kind == ConnectorFailureKind.NotFound)
        {
            return null;
        }
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("ApiIntegrationCode", config.Credentials.ApiIntegrationCode);
        req.Headers.Add("UserName", config.Credentials.UserName);
        req.Headers.Add("Secret", config.Credentials.Secret);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: JsonOpts);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorFailureKind.Timeout, "Autotask request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(ConnectorFailureKind.Timeout, "Autotask request failed.", ex);
        }

        if (!resp.IsSuccessStatusCode)
            throw MapError(resp, await SafeBodyAsync(resp, ct));

        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    /// <summary>
    /// Reads the response body (best effort) so provider validation messages reach the caller.
    /// Autotask explains WHY a create failed in an "errors" array — without it every failure reads
    /// as an opaque 500 and admins cannot tell which field is wrong.
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
                if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                    return string.Join("; ", errs.EnumerateArray().Select(e => e.ToString()));
            }
            catch (JsonException) { /* not JSON — fall through to the raw text */ }
            return raw.Length > 400 ? raw[..400] : raw;
        }
        catch { return null; }
    }

    private static ConnectorException MapError(HttpResponseMessage resp, string? body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? "" : $" {body}";
        return resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new(ConnectorFailureKind.Authentication, "Autotask rejected the credentials."),
            HttpStatusCode.Forbidden => new(ConnectorFailureKind.PermissionDenied, "Autotask denied permission."),
            HttpStatusCode.NotFound => new(ConnectorFailureKind.NotFound, "Autotask entity not found."),
            HttpStatusCode.TooManyRequests => new(ConnectorFailureKind.RateLimited, "Autotask rate limit hit.")
            {
                RetryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10),
            },
            >= HttpStatusCode.InternalServerError => new(ConnectorFailureKind.ProviderError, $"Autotask server error ({(int)resp.StatusCode}).{detail}"),
            _ => new(ConnectorFailureKind.InvalidRequest, $"Autotask rejected the request ({(int)resp.StatusCode}).{detail}"),
        };
    }

    /// <summary>
    /// Autotask answers with numeric picklist IDS, so an unmapped ticket reached the portal with a
    /// status of "1". Resolve each one to the tenant's own label before it leaves the connector —
    /// the mapping engine then has something meaningful to match on, and an unmapped value at least
    /// reads as words. Metadata is memoized, so this costs one request per sync run, not per ticket.
    /// </summary>
    private async Task<UnifiedTicket> ToUnifiedAsync(AtTicket t, CancellationToken ct) => new()
    {
        ExternalId = t.Id.ToString(),
        Title = t.Title ?? "",
        Description = t.Description,
        Status = await LabelForAsync("status", t.Status, ct),
        Priority = await LabelForAsync("priority", t.Priority, ct),
        Category = await LabelForAsync("ticketCategory", t.Category, ct),
        QueueOrBoard = await LabelForAsync("queueID", t.QueueId, ct),
        AssignedTechnicianExternalId = t.AssignedResourceId,
        RequesterExternalId = t.CompanyId.ToString(),
        // An Autotask ticket carries only a numeric companyID, so without this every client company
        // in the portal was named "Company 176" — the id, dressed up as a name. It reached the client
        // list, the ticket lists, client workload analytics and the client reports, and it looked
        // like data rather than an error. Null when unknown, so the caller keeps its own fallback
        // instead of being handed a guess.
        CompanyName = _companyNames.GetValueOrDefault(t.CompanyId),
        CreatedAt = t.CreateDate,
        ModifiedAt = t.LastActivityDate,
        ResolvedAt = t.ResolvedDateTime,
        ClosedAt = t.CompletedDate,
        // The SLA target where one applies, else the ticket's own due date. Null when Autotask
        // supplies neither — an absent target is not a met one, so nothing is invented here.
        SlaDueAt = t.ResolvedDueDateTime ?? t.DueDateTime,
    };

    private static string Hmac(string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
}
