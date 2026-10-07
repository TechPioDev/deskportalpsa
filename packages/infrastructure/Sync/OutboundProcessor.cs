using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Tickets;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// Sends one waiting change to its PSA and records what became of it.
///
/// Three things can happen, and only one of them is "synced":
///
/// - **The PSA takes it.** The operation is synced, and what was kept of the change on the
///   portal's side is completed: the note gets the PSA's id, the status the PSA accepted becomes
///   the ticket's status, the hour is recorded.
/// - **The PSA still cannot be reached.** It waits again, longer, until its tries run out.
/// - **The PSA answers and refuses**, or the change no longer makes sense. It is failed at once,
///   with the reason: trying again unchanged would only be refused again.
///
/// What is never done is send something twice because the first answer was lost. Before a note is
/// sent again after a try that may have got through, the PSA is asked whether it has it; a time
/// entry is looked for in the same way; a status is safe to set twice; and a ticket that may have
/// been created is not created again by a machine at all.
/// </summary>
public sealed class OutboundProcessor(
    DeskDbContext db, OutboundQueue queue, IConnectorResolver connectors, TicketTimeWriter time, TicketStatusWriter status,
    ITicketResyncService resync, TimeProvider clock)
{
    /// <summary>Not refused, and not done: the PSA could not be reached. Tried again later.</summary>
    private sealed class NotNow(string message, bool uncertain, TimeSpan? after = null) : Exception(message)
    {
        public bool Uncertain { get; } = uncertain;
        public TimeSpan? After { get; } = after;
    }

    /// <summary>Tries the operations that are due, oldest first. Returns how many were taken.</summary>
    public async Task<int> RunDueAsync(int take = 25, CancellationToken ct = default)
    {
        var done = 0;
        foreach (var id in await queue.DueAsync(take, ct))
            if (await ProcessAsync(id, ct) is not null) done++;
        return done;
    }

    /// <summary>Sends one operation. Null where it was not there to take (another worker has it, or it is settled).</summary>
    public async Task<OutboundState?> ProcessAsync(Guid id, CancellationToken ct = default)
    {
        var op = await queue.ClaimAsync(id, ct);
        if (op is null) return null;
        op.Attempts++;
        op.LastAttemptAt = clock.GetUtcNow();

        try
        {
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == op.TicketId, ct)
                ?? throw new ValidationFailedException("The ticket this change was for no longer exists.");
            var externalId = op.Kind switch
            {
                OutboundKind.Note => await SendNoteAsync(op, ticket, ct),
                OutboundKind.StatusChange => await SendStatusAsync(op, ticket, ct),
                OutboundKind.TimeEntry => await SendTimeAsync(op, ticket, ct),
                OutboundKind.TicketCreate => await SendCreateAsync(op, ticket, ct),
                _ => throw new ValidationFailedException("This kind of change is not one the portal sends."),
            };
            await queue.SyncedAsync(op, externalId, ct);
            return OutboundState.Synced;
        }
        catch (NotNow later)
        {
            return await SettleAsync(op, await queue.RetryLaterAsync(op, later.Message, later.Uncertain, later.After, ct), ct);
        }
        catch (ConnectorException ex) when (ex.IsTransient)
        {
            return await SettleAsync(op, await queue.RetryLaterAsync(op, Unreachable(ex), !ex.NeverSent, ex.RetryAfter, ct), ct);
        }
        catch (ConnectorException ex)
        {
            // The PSA answered, and the answer was no.
            return await SettleAsync(op, await queue.FailAsync(op, ex.Message, ct), ct);
        }
        catch (DeskException ex)
        {
            return await SettleAsync(op, await queue.FailAsync(op, ex.Message, ct), ct);
        }
    }

    /// <summary>
    /// A try that ended in a fault of the portal's own (not the PSA's answer, and not the PSA being
    /// away) is still a try: counted, and tried again later, so that such a change runs out of
    /// tries and is failed and kept like any other, instead of being taken again every few minutes
    /// for ever. Whether anything reached the PSA is not known, so it is treated as possibly sent.
    /// Called on a unit of work of its own, since the one that failed is not to be saved.
    /// </summary>
    public async Task<OutboundState?> CrashedAsync(Guid id, CancellationToken ct = default)
    {
        var op = await db.OutboundOperations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (op is null || op.State != OutboundState.Pending) return op?.State;
        op.Attempts++;
        op.LastAttemptAt = clock.GetUtcNow();
        const string fault = "The portal met a fault of its own while sending this. The details are in the portal's log.";
        return await SettleAsync(op, await queue.RetryLaterAsync(op, fault, uncertain: true, null, ct), ct);
    }

    /// <summary>What the PSA being away reads as, for the person whose change is waiting.</summary>
    public static string Unreachable(ConnectorException ex) => ex.Kind switch
    {
        ConnectorFailureKind.RateLimited => "The PSA is taking no more requests for the moment.",
        ConnectorFailureKind.ProviderError => "The PSA reported a fault of its own.",
        _ => "The PSA did not answer.",
    };

    /// <summary>Once an operation has failed for good, what it left on the portal's side says so too.</summary>
    private async Task<OutboundState> SettleAsync(OutboundOperation op, OutboundState state, CancellationToken ct)
    {
        if (state != OutboundState.Failed) return state;
        switch (op.Kind)
        {
            case OutboundKind.Note:
                await queue.SetNoteStateAsync(op, OutboundState.Failed, ct);
                break;
            case OutboundKind.TimeEntry when op.TargetId is { } entryId:
                if (await db.TicketTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct) is { SyncStatus: not TimeEntrySyncStatus.Synced } entry)
                {
                    entry.SyncStatus = TimeEntrySyncStatus.Failed;
                    entry.SyncError = op.LastError;
                }
                break;
            case OutboundKind.TicketCreate:
                if (await db.Tickets.FirstOrDefaultAsync(t => t.Id == op.TicketId, ct) is { SyncStatus: not TicketSyncStatus.Synced } unsent)
                {
                    unsent.SyncStatus = TicketSyncStatus.Error;
                    unsent.SyncError = op.LastError;
                }
                break;
        }
        await db.SaveChangesAsync(ct);
        return state;
    }

    // ---- a note ------------------------------------------------------------------------------

    private async Task<string?> SendNoteAsync(OutboundOperation op, Ticket ticket, CancellationToken ct)
    {
        var sent = OutboundQueue.Payload<OutboundNote>(op);
        var note = await db.TicketNotes.FirstOrDefaultAsync(n => n.Id == sent.NoteId, ct)
            ?? throw new ValidationFailedException("The note this was for no longer exists.");
        if (!string.IsNullOrEmpty(note.ExternalNoteId))
        {
            note.SyncState = OutboundState.Synced;
            return note.ExternalNoteId;
        }
        if (string.IsNullOrEmpty(ticket.ExternalTicketId))
            throw new NotNow("The ticket itself has not reached the PSA yet.", uncertain: false);

        var connector = await connectors.ResolveAsync(op.PsaConnectionId, ct);
        string? externalId = null;
        if (op.Uncertain)
        {
            // An earlier try may have got through with its answer lost. Sent again it would be said
            // to the customer twice, so the PSA is asked first whether it has it: the same words,
            // the same side of the conversation, written no earlier than this note was, and not
            // already known here as another of the portal's notes.
            var taken = (await db.TicketNotes.AsNoTracking()
                    .Where(n => n.TicketId == ticket.Id && n.Id != note.Id && n.ExternalNoteId != null && !n.ImportedFromProvider)
                    .Select(n => n.ExternalNoteId!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            externalId = (await connector.GetNotesAsync(ticket.ExternalTicketId, ct))
                .Where(n => !string.IsNullOrEmpty(n.ExternalId) && !taken.Contains(n.ExternalId!) && n.IsPublic == sent.IsPublic
                    && SameWords(n.Body, note.Body) && n.CreatedAt >= note.NoteCreatedAt.AddMinutes(-5))
                .OrderBy(n => n.CreatedAt)
                .Select(n => n.ExternalId)
                .FirstOrDefault();
        }

        if (externalId is null)
        {
            var result = await connector.AddPublicNoteAsync(ticket.ExternalTicketId,
                new UnifiedTicketNoteCreateRequest(note.Body, IsPublic: sent.IsPublic, op.IdempotencyKey)
                {
                    EmailContact = sent.EmailContact, EmailCc = sent.EmailCc,
                }, ct);
            if (!result.Success) throw new ValidationFailedException(result.Error ?? "The PSA rejected the note.");
            externalId = result.ExternalId;
        }

        // A sync may have read the PSA's copy in the meantime and kept it as one of the PSA's own.
        // This note is that note: the copy goes, so the thread says it once.
        if (!string.IsNullOrEmpty(externalId))
            db.TicketNotes.RemoveRange(await db.TicketNotes
                .Where(n => n.TicketId == ticket.Id && n.Id != note.Id && n.ExternalNoteId == externalId).ToListAsync(ct));
        note.ExternalNoteId = externalId;
        note.SyncState = OutboundState.Synced;
        // The first time the team answered, for a first-response promise: when the customer could
        // read it, which is now, and not when it was typed.
        if (note.IsPublic && !note.AuthoredByClient) ticket.FirstRespondedAt ??= clock.GetUtcNow();
        return externalId;
    }

    private static bool SameWords(string? a, string? b)
        => string.Equals(Squash(a), Squash(b), StringComparison.Ordinal);

    private static string Squash(string? text)
        => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---- a status ----------------------------------------------------------------------------

    private async Task<string?> SendStatusAsync(OutboundOperation op, Ticket ticket, CancellationToken ct)
    {
        var asked = OutboundQueue.Payload<OutboundStatus>(op);
        // It got there some other way while this waited: there is nothing left to send.
        if (string.Equals(ticket.PortalStatus, asked.To, StringComparison.Ordinal)) return null;
        // The ticket has moved on since it was asked. The PSA holds a ticket's status; what somebody
        // wanted an hour ago is not set over what it says now.
        if (!string.Equals(ticket.PortalStatus, asked.From, StringComparison.Ordinal))
            throw new ValidationFailedException(
                $"The ticket was {asked.From} when the change to {asked.To} was asked for, and has since become {ticket.PortalStatus}. It was not sent.");
        await status.SetAsync(ticket, asked.To, asked.Resolution, ct, countReopen: true, actorUserId: asked.ActorUserId);
        return null;
    }

    // ---- an hour -----------------------------------------------------------------------------

    private async Task<string?> SendTimeAsync(OutboundOperation op, Ticket ticket, CancellationToken ct)
    {
        var logged = OutboundQueue.Payload<OutboundTime>(op);
        var entry = await db.TicketTimeEntries.FirstOrDefaultAsync(e => e.Id == logged.EntryId, ct)
            ?? throw new ValidationFailedException("The time entry this was for no longer exists.");
        if (entry.SyncStatus == TimeEntrySyncStatus.Synced) return entry.ExternalEntryId;

        var connector = await connectors.ResolveAsync(op.PsaConnectionId, ct);
        // The writer looks for the entry in the PSA before sending it a second time.
        var pushed = await time.PushDetailedAsync(entry, ticket, connector, ct, fromQueue: true);
        if (pushed.Ok) return entry.ExternalEntryId;
        if (pushed.Transient) throw new NotNow(pushed.Error ?? "The PSA did not answer.", uncertain: true);
        throw new ValidationFailedException(pushed.Error ?? "The PSA rejected the time entry.");
    }

    // ---- a ticket ----------------------------------------------------------------------------

    private async Task<string?> SendCreateAsync(OutboundOperation op, Ticket ticket, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ticket.ExternalTicketId) && ticket.SyncStatus == TicketSyncStatus.Synced) return ticket.ExternalTicketId;
        // An earlier try may have reached the PSA. There is no asking a PSA whether it created a
        // ticket, so this is not sent again until a person has looked and said so.
        if (op.Uncertain)
            throw new ValidationFailedException(
                "It is not known whether the PSA created this ticket on an earlier try. Look for it in the PSA, and send it again only if it is not there.");
        var result = await resync.ResyncAsync(ticket.Id, ct);
        if (result.Success) return result.ExternalTicketId;
        if (result.Transient && result.NeverSent)
        {
            // Still waiting, not failed: the resync marks a ticket it could not send as in error.
            ticket.SyncStatus = TicketSyncStatus.PendingCreate;
            throw new NotNow(result.Error ?? "The PSA did not answer.", uncertain: false);
        }
        // It may have been created with the answer lost. A second ticket for the same request is
        // worse than one that waits: this is for a person to check in the PSA and then send again.
        throw new ValidationFailedException(result.Transient
            ? $"It is not known whether the PSA created this ticket: {result.Error} Look for it in the PSA, and retry only if it is not there."
            : result.Error ?? "The PSA rejected the ticket.");
    }
}
