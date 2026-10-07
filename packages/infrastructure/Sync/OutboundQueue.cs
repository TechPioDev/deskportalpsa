using System.Text.Json;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Resilience;
using Desk.Domain.Sync;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Sync;

/// <summary>
/// The changes waiting to reach a PSA: putting one in, taking one to send, and what becomes of it.
///
/// A change is put here when the PSA could not be reached at the moment it was made. It is tried
/// again with a longer wait each time (thirty seconds, then double, up to an hour, each spread by
/// a fifth so a PSA coming back is not met by everything at once), or when the PSA said to come
/// back, if it said. After eight tries it is failed, and kept.
///
/// Of one ticket's changes, the oldest goes first: a status asked for after a reply is not sent
/// before the reply.
///
/// An operation is taken by a save of its own, checked by its version and held for a lease, as a
/// background job is: of two workers one is refused, and a worker that stops does not strand it.
/// </summary>
public sealed class OutboundQueue(DeskDbContext db, TimeProvider clock, IAuditWriter? audit = null, Func<double>? jitter = null)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>The wait before each further try: 30 s, 1 min, 2, 4, 8, 16, 32 minutes, then an hour.</summary>
    public static readonly RetryPolicy Backoff = new()
    {
        BaseDelay = TimeSpan.FromSeconds(30), Multiplier = 2, MaxDelay = TimeSpan.FromHours(1), UseJitter = true,
    };

    public const string PendingLabel = "Pending Sync";
    public const string SyncedLabel = "Synced";
    public const string FailedLabel = "Sync Failed";

    public static string Label(OutboundState state) => state switch
    {
        OutboundState.Synced => SyncedLabel,
        OutboundState.Failed => FailedLabel,
        _ => PendingLabel,
    };

    private readonly Func<double> _jitter = jitter ?? (() => Random.Shared.NextDouble() * 2 - 1);

    /// <summary>
    /// Keeps a change that could not be sent. Added to the unit of work and NOT saved: the caller
    /// saves it with whatever it kept of the change on the portal's side, so that the two are
    /// stored together or not at all. The try that has just failed is the first.
    /// </summary>
    public OutboundOperation Enqueue(
        Ticket ticket, OutboundKind kind, string summary, object payload, Guid? targetId, string idempotencyKey,
        Guid? byAppUser, Guid? byClientUser, string? byName, string? firstError, bool uncertain)
    {
        if (ticket.PsaConnectionId is not { } connectionId)
            throw new InvalidOperationException("Only a change to a PSA ticket waits for a PSA.");
        var now = clock.GetUtcNow();
        var op = new OutboundOperation
        {
            MspOrganizationId = ticket.MspOrganizationId, PsaConnectionId = connectionId, TicketId = ticket.Id,
            Kind = kind, State = OutboundState.Pending, Summary = Limit(summary, 300),
            PayloadJson = JsonSerializer.Serialize(payload), TargetId = targetId, IdempotencyKey = idempotencyKey,
            Attempts = 1, LastAttemptAt = now, LastError = firstError is null ? null : Limit(firstError, 2000), Uncertain = uncertain,
            NextAttemptAt = now + Backoff.ComputeDelay(1, _jitter()),
            RequestedByAppUserId = byAppUser, RequestedByClientUserId = byClientUser, RequestedByName = byName is null ? null : Limit(byName, 200),
        };
        db.OutboundOperations.Add(op);
        return op;
    }

    /// <summary>Recorded once the caller has saved: who put what in the queue, and why.</summary>
    public Task AuditQueuedAsync(OutboundOperation op, CancellationToken ct = default)
        => audit is null ? Task.CompletedTask
            : audit.WriteAsync("outbound.queued", "Ticket", op.TicketId.ToString(),
                new { operationId = op.Id, kind = op.Kind.ToString(), op.Summary, reason = op.LastError, by = op.RequestedByName }, ct);

    /// <summary>
    /// The operations to try now, oldest first: pending, their time come, held by no worker, and
    /// with no older change of the same ticket still waiting. Only ids; each is read and checked
    /// again when it is taken.
    /// </summary>
    public async Task<List<Guid>> DueAsync(int take, CancellationToken ct = default)
        => (await DueWithOrganizationAsync(take, ct)).Select(d => d.Id).ToList();

    /// <summary>
    /// The same, each with the organization it belongs to. The worker asks this with every
    /// organization in view and then takes each change inside its own organization's scope, so
    /// that what is done and what is recorded while sending it belongs to that organization.
    /// </summary>
    public async Task<List<OutboundDue>> DueWithOrganizationAsync(int take, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return await db.OutboundOperations.AsNoTracking()
            .Where(o => o.State == OutboundState.Pending
                && (o.NextAttemptAt == null || o.NextAttemptAt <= now)
                && (o.LeaseExpiresAt == null || o.LeaseExpiresAt < now)
                && !db.OutboundOperations.Any(e => e.TicketId == o.TicketId && e.State == OutboundState.Pending && e.CreatedAt < o.CreatedAt))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Take(take)
            .Select(o => new OutboundDue(o.Id, o.MspOrganizationId))
            .ToListAsync(ct);
    }

    /// <summary>Takes the operation for this worker, or null where it is no longer there to take.</summary>
    public async Task<OutboundOperation?> ClaimAsync(Guid id, CancellationToken ct = default)
    {
        var op = await db.OutboundOperations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (op is null) return null;
        var now = clock.GetUtcNow();
        var due = op.State == OutboundState.Pending
            && (op.NextAttemptAt is null || op.NextAttemptAt <= now)
            && (op.LeaseExpiresAt is null || op.LeaseExpiresAt < now);
        if (!due)
        {
            db.Entry(op).State = EntityState.Detached;
            return null;
        }
        op.LeaseExpiresAt = now + Lease;
        op.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(op).State = EntityState.Detached;
            return null;
        }
        return op;
    }

    /// <summary>The PSA has it and said so. The only way an operation becomes synced.</summary>
    public async Task SyncedAsync(OutboundOperation op, string? externalId, CancellationToken ct = default)
    {
        (op.State, op.SyncedAt, op.ExternalId) = (OutboundState.Synced, clock.GetUtcNow(), externalId);
        (op.LastError, op.NextAttemptAt, op.LeaseExpiresAt, op.Uncertain) = (null, null, null, false);
        op.Version++;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The PSA could not be reached again. Tried later, unless that was the last try: then it is
    /// failed and kept, with why.
    /// </summary>
    public async Task<OutboundState> RetryLaterAsync(OutboundOperation op, string error, bool uncertain, TimeSpan? comeBackAfter, CancellationToken ct = default)
    {
        op.LastError = Limit(error, 2000);
        op.Uncertain = op.Uncertain || uncertain;
        if (op.Attempts >= op.MaxAttempts)
            return await FailAsync(op, $"The PSA could not be reached in {op.Attempts} tries. The last one: {error}", ct);
        // What the PSA asked for, when it asked; otherwise the next step of the backoff.
        var wait = comeBackAfter is { } asked && asked > TimeSpan.Zero ? asked : Backoff.ComputeDelay(op.Attempts, _jitter());
        (op.NextAttemptAt, op.LeaseExpiresAt) = (clock.GetUtcNow() + wait, null);
        op.Version++;
        await db.SaveChangesAsync(ct);
        return OutboundState.Pending;
    }

    /// <summary>Failed: refused by the PSA, or out of tries. Kept, for somebody to retry or let go of.</summary>
    public async Task<OutboundState> FailAsync(OutboundOperation op, string error, CancellationToken ct = default)
    {
        (op.State, op.LastError, op.NextAttemptAt, op.LeaseExpiresAt) = (OutboundState.Failed, Limit(error, 2000), null, null);
        op.Version++;
        await db.SaveChangesAsync(ct);
        if (audit is not null)
            await audit.WriteAsync("outbound.failed", "Ticket", op.TicketId.ToString(),
                new { operationId = op.Id, kind = op.Kind.ToString(), op.Summary, op.Attempts, error = op.LastError, by = op.RequestedByName }, ct);
        return OutboundState.Failed;
    }

    /// <summary>
    /// Sends a failed operation round again, from the first try, or brings a waiting one forward
    /// to now. Somebody decided to: who, is audited.
    /// </summary>
    public async Task<OutboundOperation> RetryAsync(Guid id, string? byName, CancellationToken ct = default)
    {
        var op = await db.OutboundOperations.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Outbound change");
        if (op.State == OutboundState.Synced) throw new ValidationFailedException("This change is already in the PSA.");
        // A worker has it at this moment. Freed now it could be taken by a second and sent twice.
        if (op.State == OutboundState.Pending && op.LeaseExpiresAt is { } held && held > clock.GetUtcNow())
            throw new ValidationFailedException("This change is being sent at this moment. Try again in a minute.");
        var was = op.State;
        if (was == OutboundState.Failed)
        {
            op.Attempts = 0;
            // A ticket that may already have been created is not created again by a machine. Somebody
            // sending it again has looked in the PSA: that is the decision the doubt was waiting for.
            if (op.Kind == OutboundKind.TicketCreate) op.Uncertain = false;
        }
        (op.State, op.NextAttemptAt, op.LeaseExpiresAt) = (OutboundState.Pending, null, null);
        op.Version++;
        await SetNoteStateAsync(op, OutboundState.Pending, ct);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
            await audit.WriteAsync("outbound.retried", "Ticket", op.TicketId.ToString(),
                new { operationId = op.Id, kind = op.Kind.ToString(), op.Summary, was = Label(was), lastError = op.LastError, by = byName }, ct);
        return op;
    }

    /// <summary>
    /// Lets go of a change that has not reached the PSA. It is not sent. A reply that was never
    /// sent is removed with it, so the thread does not go on showing words the customer never got;
    /// what it was, and who let go of it, is in the audit log.
    /// </summary>
    public async Task DiscardAsync(Guid id, string? byName, CancellationToken ct = default)
    {
        var op = await db.OutboundOperations.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Outbound change");
        if (op.State == OutboundState.Synced) throw new ValidationFailedException("This change is already in the PSA and cannot be taken back from here.");
        var now = clock.GetUtcNow();
        if (op.State == OutboundState.Pending && op.LeaseExpiresAt is { } held && held > now)
            throw new ValidationFailedException("This change is being sent at this moment. Try again in a minute.");

        if (op.Kind == OutboundKind.Note && op.TargetId is { } noteId
            && await db.TicketNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.ExternalNoteId == null, ct) is { } unsent)
            db.TicketNotes.Remove(unsent);
        db.OutboundOperations.Remove(op);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
            await audit.WriteAsync("outbound.discarded", "Ticket", op.TicketId.ToString(),
                new { operationId = op.Id, kind = op.Kind.ToString(), op.Summary, was = Label(op.State), op.Attempts, lastError = op.LastError, madeBy = op.RequestedByName, by = byName }, ct);
    }

    /// <summary>A note says on itself where its sending stands, so the thread can be read without the queue.</summary>
    public async Task SetNoteStateAsync(OutboundOperation op, OutboundState state, CancellationToken ct = default)
    {
        if (op.Kind != OutboundKind.Note || op.TargetId is not { } noteId) return;
        if (await db.TicketNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct) is { } note) note.SyncState = state;
    }

    public static T Payload<T>(OutboundOperation op) where T : class
        => JsonSerializer.Deserialize<T>(op.PayloadJson) ?? throw new ValidationFailedException("This change can no longer be read.");

    private static string Limit(string? text, int length)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..length];
}

/// <summary>A change that is due, and whose it is.</summary>
public sealed record OutboundDue(Guid Id, Guid MspOrganizationId);

/// <summary>What is needed to send a note that waited.</summary>
public sealed record OutboundNote(Guid NoteId, bool IsPublic, bool EmailContact, IReadOnlyList<string> EmailCc);

/// <summary>A status somebody asked for, and what the ticket's status was when they did.</summary>
public sealed record OutboundStatus(string To, string From, string? Resolution, Guid? ActorUserId);

/// <summary>An hour logged in the portal that has not reached the PSA.</summary>
public sealed record OutboundTime(Guid EntryId);

/// <summary>A ticket raised in the portal that the PSA has not got.</summary>
public sealed record OutboundCreate(Guid TicketId);
