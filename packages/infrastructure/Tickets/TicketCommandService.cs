using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Client-portal writes. The PSA is the system of record, so create/comment go to the provider
/// first; only on success is the portal projection written. Each write records a portal-origin
/// sync event (with the resulting state hash) so the inbound sync recognises and skips its own echo.
/// </summary>
public sealed class TicketCommandService(
    DeskDbContext db,
    IConnectorResolver connectors,
    IMappingEngine mapping,
    ISyncEventStore syncEvents,
    ITicketScopeQuery scopeQuery,
    TimeProvider clock,
    Desk.Application.Analytics.IActivityRecorder activity) : ITicketCommandService
{
    /// <summary>Portal-neutral status a newly raised ticket starts in.</summary>
    private const string NewStatus = "NEW";

    /// <summary>
    /// Who a public reply on this ticket can be sent to: the contacts of the ticket's OWN client
    /// company, read live from the provider, plus whether that provider lets the caller choose at
    /// all. Deliberately lives beside the write it feeds — the picker the technician sees and the
    /// allow-list the write enforces resolve through the same call, so they cannot drift into a
    /// state where the UI offers an address the API then refuses (or worse, accepts).
    /// </summary>
    public async Task<ReplyRecipientsDto> ListReplyRecipientsAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await scopeQuery.FindAsync(db.Tickets, ticketId, appUserId, Permissions.TicketsAddPublicNote, ct)
            ?? throw new NotFoundException("Ticket");
        var company = await db.ClientCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ticket.ClientCompanyId, ct)
            ?? throw new NotFoundException("Client company");
        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        var caps = await connector.GetCapabilitiesAsync(ct);

        var contacts = await CompanyContactsAsync(ticket, ct);
        return new ReplyRecipientsDto(
            company.Name,
            caps.SupportsNoteEmailRecipients,
            contacts.Select(c => new ReplyRecipientDto(c.ExternalId, c.DisplayName, c.Email)).ToList());
    }

    /// <summary>
    /// The ticket's client company's contacts, from the provider. Scoped by the company's OWN
    /// external id — never a company id from the request — and filtered to active contacts that
    /// actually have an address, because an entry with no email is not a recipient.
    /// A provider that cannot answer yields an empty list rather than an exception: a reply must
    /// still be postable when the contact lookup is unavailable.
    /// </summary>
    private async Task<IReadOnlyList<ExternalContact>> CompanyContactsAsync(Ticket ticket, CancellationToken ct)
    {
        var company = await db.ClientCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ticket.ClientCompanyId, ct);
        if (company is null || string.IsNullOrWhiteSpace(company.ExternalCompanyId)) return [];
        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        try
        {
            var contacts = await connector.GetContactsAsync(company.ExternalCompanyId, ct);
            return contacts.Where(c => c.IsActive && !string.IsNullOrWhiteSpace(c.Email)).ToList();
        }
        catch (ConnectorException) { return []; }
    }

    private async Task<HashSet<string>> CompanyContactEmailsAsync(Ticket ticket, CancellationToken ct)
        => (await CompanyContactsAsync(ticket, ct))
            .Select(c => c.Email.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<CreateTicketResultDto> CreateAsync(ClientAccess access, CreateTicketInput input, CancellationToken ct = default)
    {
        var company = await db.ClientCompanies.FirstOrDefaultAsync(c => c.Id == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Client company");
        var connection = await db.PsaConnections.FirstOrDefaultAsync(c => c.Id == company.PsaConnectionId, ct)
            ?? throw new NotFoundException("PSA connection");
        var requester = await db.ClientUsers.FirstOrDefaultAsync(u => u.Id == access.ClientUserId, ct)
            ?? throw new NotFoundException("Client user");

        var rules = await LoadRulesAsync(access.MspOrganizationId, connection.Provider, ct);
        var ctx = new MappingContext
        {
            Provider = connection.Provider, PsaConnectionId = connection.Id,
            ClientCompanyId = company.Id, QueueOrBoardKey = input.QueueOrBoard,
        };

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var connector = await connectors.ResolveAsync(connection.Id, ct);
        CreateTicketResult created;
        try
        {
            created = await connector.CreateTicketAsync(new UnifiedTicketCreateRequest
        {
            Title = input.Title,
            Description = input.Description,
            // The portal row starts at NEW, so tell the provider the same thing. Some PSAs
            // (Autotask) reject a create without a status, and leaving it unset also meant the
            // provider chose its own default — diverging from the portal on the very first write.
            Status = MapOut(rules, ctx, "status", NewStatus),
            Priority = MapOut(rules, ctx, "priority", input.Priority),
            Category = MapOut(rules, ctx, "category", input.Category),
            // Queue: use the caller's choice when given, else the connection's configured default
            // (already a provider id, so it bypasses mapping). Providers such as Autotask require one.
            QueueOrBoard = input.QueueOrBoard is not null
                ? MapOut(rules, ctx, "queue", input.QueueOrBoard)
                : connection.DefaultQueueOrBoardId,
            TicketType = connection.DefaultTicketType,
            IssueType = connection.DefaultIssueType,
            SubIssueType = connection.DefaultSubIssueType,
            ExternalCompanyId = company.ExternalCompanyId,
            RequesterExternalId = requester.ExternalContactId,
            RequesterEmail = requester.Email,
            IdempotencyKey = idempotencyKey,
            }, ct);
        }
        catch (ConnectorException ex)
        {
            // An unreachable or rate-limited PSA is not the customer's problem to retype.
            created = new CreateTicketResult(false, null, ex.Message);
        }

        var now = clock.GetUtcNow();
        var ticket = new Ticket
        {
            MspOrganizationId = access.MspOrganizationId,
            PsaConnectionId = connection.Id,
            Provider = connection.Provider,
            ExternalTicketId = created.ExternalId,
            ClientCompanyId = company.Id,
            RequesterUserId = requester.Id,
            RequesterName = requester.DisplayName,
            RequesterEmail = requester.Email,
            Title = input.Title,
            Description = input.Description,
            PortalStatus = NewStatus,
            PortalPriority = input.Priority ?? "NORMAL",
            PortalCategory = input.Category,
            QueueOrBoard = input.QueueOrBoard,
            // Recorded either way. A rejected create used to throw before anything was written, so
            // the customer's ticket vanished with nothing to retry from and no count of what was
            // lost. It is kept here as Error, listed for staff, and resyncable.
            SyncStatus = created.Success ? TicketSyncStatus.Synced : TicketSyncStatus.Error,
            SyncError = created.Success ? null : created.Error,
            LastSyncedAt = created.Success ? now : null,
            CorrelationId = Guid.NewGuid(),
        };
        ticket.UpdateHash = HashOf(ticket);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        if (!created.Success)
        {
            // Still surfaced as a failure: the customer must not be told it reached the PSA when it
            // did not. The row exists so the work can be recovered rather than retyped.
            await RecordPortalEventAsync(access.MspOrganizationId, connection.Id, ticket, idempotencyKey, "ticket.create_failed", ct);
            throw new ValidationFailedException(created.Error ?? "The PSA rejected the ticket.");
        }

        await RecordPortalEventAsync(access.MspOrganizationId, connection.Id, ticket, idempotencyKey, "ticket.created", ct);
        await activity.RecordAsync(new Desk.Application.Analytics.ActivityRecord(
            Desk.Domain.Analytics.ActivityKind.TicketCreated, Desk.Domain.Analytics.ActivitySource.Portal)
        {
            MspOrganizationId = access.MspOrganizationId,
            ActorUserId = null,            // raised by a CLIENT user, who is not an AppUser
            PsaConnectionId = connection.Id,
            TicketId = ticket.Id,
            ClientCompanyId = company.Id,
            Detail = "Raised in the client portal",
        }, ct);
        return new CreateTicketResultDto(ticket.Id, created.ExternalId);
    }

    public async Task<TicketNoteDto> AddCommentAsync(ClientAccess access, Guid ticketId, string body, CancellationToken ct = default)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t =>
            t.Id == ticketId
            && t.ClientCompanyId == access.ClientCompanyId
            && (access.IsCompanyAdministrator || t.RequesterUserId == access.ClientUserId), ct)
            ?? throw new NotFoundException("Ticket");
        var requester = await db.ClientUsers.FirstOrDefaultAsync(u => u.Id == access.ClientUserId, ct)
            ?? throw new NotFoundException("Client user");

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        var result = await connector.AddPublicNoteAsync(
            ticket.ExternalTicketId!, new UnifiedTicketNoteCreateRequest(body, IsPublic: true, idempotencyKey), ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the comment.");

        var now = clock.GetUtcNow();
        var note = new TicketNote
        {
            MspOrganizationId = access.MspOrganizationId,
            TicketId = ticket.Id,
            ExternalNoteId = result.ExternalId,
            AuthorName = requester.DisplayName,
            AuthoredByClient = true,
            Body = body,
            IsPublic = true,
            NoteCreatedAt = now,
            OriginCorrelationId = ticket.CorrelationId,
        };
        db.TicketNotes.Add(note);
        await db.SaveChangesAsync(ct);

        await RecordPortalEventAsync(access.MspOrganizationId, ticket.PsaConnectionId, ticket, idempotencyKey, "note.created", ct);
        await activity.RecordAsync(new Desk.Application.Analytics.ActivityRecord(
            Desk.Domain.Analytics.ActivityKind.NoteAdded, Desk.Domain.Analytics.ActivitySource.Portal)
        {
            MspOrganizationId = access.MspOrganizationId,
            OccurredAt = note.NoteCreatedAt,
            PsaConnectionId = ticket.PsaConnectionId,
            TicketId = ticket.Id,
            ClientCompanyId = ticket.ClientCompanyId,
            Detail = "Client reply",
        }, ct);
        return new TicketNoteDto(note.Id, note.AuthorName, true, note.Body, note.NoteCreatedAt);
    }

    /// <summary>
    /// A technician reply from the staff dashboard. Same provider push and echo bookkeeping as a
    /// client comment; only the attribution differs — the staff display name, never a client flag.
    /// isPublic=false posts an INTERNAL note: pushed to the PSA flagged internal (ConnectWise
    /// internalAnalysisFlag / Autotask internal publish value) and stored IsPublic=false, so the
    /// client read path never returns it. Only the staff endpoint can reach this parameter.
    /// </summary>
    public async Task<TicketNoteDto> AddStaffCommentAsync(Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic = true, CancellationToken ct = default)
        => await AddStaffCommentAsync(appUserId, authorName, ticketId, body, isPublic, emailContact: false, emailCc: [], ct);

    /// <inheritdoc cref="AddStaffCommentAsync(Guid, string, Guid, string, bool, CancellationToken)"/>
    /// <param name="emailContact">Ask the PSA to email the ticket's own contact.</param>
    /// <param name="emailCc">Extra addresses to copy — validated against THIS ticket's company.</param>
    public async Task<TicketNoteDto> AddStaffCommentAsync(
        Guid appUserId, string authorName, Guid ticketId, string body, bool isPublic,
        bool emailContact, IReadOnlyList<string> emailCc, CancellationToken ct = default)
    {
        var ticket = await scopeQuery.FindAsync(db.Tickets, ticketId, appUserId, Permissions.TicketsAddPublicNote, ct)
            ?? throw new NotFoundException("Ticket");
        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new ValidationFailedException("This ticket is not yet synced to the PSA, so a reply cannot be posted.");

        // An internal note is never mailed to anyone, whatever the caller asked for. The composer
        // already hides the recipients when internal is selected; this is the gate that matters,
        // because a request can be made without the composer.
        if (!isPublic) { emailContact = false; emailCc = []; }

        // Every copied address must belong to THIS ticket's client company. Trusting the request
        // would turn one customer's reply into a way to mail another's contacts — the isolation
        // the portal exists to guarantee. Checked against the provider's own contact list, not a
        // list the browser sent back to us.
        if (emailCc.Count > 0)
        {
            var allowed = await CompanyContactEmailsAsync(ticket, ct);
            var rejected = emailCc.Where(a => !allowed.Contains(a.Trim())).ToList();
            if (rejected.Count > 0)
                throw new ValidationFailedException(
                    $"These addresses are not contacts of this ticket's customer: {string.Join(", ", rejected)}.");
        }

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var connector = await connectors.ResolveAsync(ticket.PsaConnectionId, ct);
        var result = await connector.AddPublicNoteAsync(
            ticket.ExternalTicketId,
            new UnifiedTicketNoteCreateRequest(body, IsPublic: isPublic, idempotencyKey)
            {
                EmailContact = emailContact,
                EmailCc = emailCc,
            }, ct);
        if (!result.Success)
            throw new ValidationFailedException(result.Error ?? "The PSA rejected the comment.");

        var note = new TicketNote
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            ExternalNoteId = result.ExternalId,
            AuthorName = authorName,
            AuthoredByClient = false,
            Body = body,
            IsPublic = isPublic,
            NoteCreatedAt = clock.GetUtcNow(),
            OriginCorrelationId = ticket.CorrelationId,
        };
        db.TicketNotes.Add(note);
        await db.SaveChangesAsync(ct);

        await RecordPortalEventAsync(ticket.MspOrganizationId, ticket.PsaConnectionId, ticket, idempotencyKey, "note.created", ct);
        await activity.RecordAsync(new Desk.Application.Analytics.ActivityRecord(
            Desk.Domain.Analytics.ActivityKind.NoteAdded, Desk.Domain.Analytics.ActivitySource.Portal)
        {
            MspOrganizationId = ticket.MspOrganizationId,
            OccurredAt = note.NoteCreatedAt,
            ActorUserId = appUserId,
            PsaConnectionId = ticket.PsaConnectionId,
            TicketId = ticket.Id,
            ClientCompanyId = ticket.ClientCompanyId,
            Detail = isPublic ? "Public reply" : "Internal note",
        }, ct);
        return new TicketNoteDto(note.Id, note.AuthorName, false, note.Body, note.NoteCreatedAt);
    }

    private async Task<List<FieldMapping>> LoadRulesAsync(Guid org, ProviderType provider, CancellationToken ct)
        => await db.FieldMappings.AsNoTracking()
            .Where(m => m.Provider == provider && m.IsActive)
            .ToListAsync(ct);

    private string? MapOut(List<FieldMapping> rules, MappingContext ctx, string field, string? value)
    {
        if (value is null) return null;
        var r = mapping.MapToProvider(rules, ctx, field, value);
        return r.Resolved ? r.Value : value; // passthrough when unmapped
    }

    private static string HashOf(Ticket t) => UpdateHasher.ForTicketState(
        t.PortalStatus, t.PortalPriority, t.PortalCategory, t.Title, t.Description,
        t.ResolvedAt, t.ClosedAt, t.SlaDueAt, t.PsaCreatedAt, t.QueueOrBoard,
        // As the sync hashes them: the provider's contact, or nothing where only a placeholder is held,
        // so the portal's own write still recognises itself when it comes back.
        TicketContact.Name(t), TicketContact.Email(t));

    private Task RecordPortalEventAsync(Guid org, Guid connId, Ticket ticket, string idemKey, string eventType, CancellationToken ct)
        => syncEvents.TryRegisterAsync(new SyncEventRegistration
        {
            MspOrganizationId = org,
            PsaConnectionId = connId,
            TicketId = ticket.Id,
            EventType = eventType,
            IdempotencyKey = idemKey,
            SourceMarker = SyncSource.Portal,
            CorrelationId = ticket.CorrelationId,
            PayloadHash = ticket.UpdateHash,
            OccurredAt = clock.GetUtcNow(),
        }, ct);
}
