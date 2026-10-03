using System.Collections.Concurrent;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Desk.Infrastructure.Workforce;

/// <summary>
/// One person's plan is changed by one request at a time.
///
/// Availability shown on a screen is not a reservation: two planners can both be told 15:00-16:00
/// is free and both try to take it. Whoever holds the gate for that person checks conflicts against
/// what is REALLY there now and writes; the other waits, then sees the first booking and is refused.
///
/// On PostgreSQL the gate is a row lock on the person (SELECT ... FOR UPDATE) inside the request's
/// transaction, so it holds across every API container at once - two of them serve during a
/// deploy. Everywhere else (SQLite in local mode, the in-memory provider in tests) the application
/// runs as one process, and an in-process lock per person is the same guarantee.
/// </summary>
public sealed class PlanningGate(DeskDbContext db)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Local = new();

    public async Task<Hold> HoldAsync(Guid appUserId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlAsync($"SELECT \"Id\" FROM app_users WHERE \"Id\" = {appUserId} FOR UPDATE", ct);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
            return new Hold(transaction, null);
        }
        var gate = Local.GetOrAdd(appUserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return new Hold(db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null, gate);
        }
        catch
        {
            // A transaction that would not open must not leave this person locked for the life of the process.
            gate.Release();
            throw;
        }
    }

    /// <summary>The gate while held. Commit to keep what was written; disposing without committing throws it away.</summary>
    public sealed class Hold(IDbContextTransaction? transaction, SemaphoreSlim? local) : IAsyncDisposable
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            if (transaction is not null) await transaction.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (transaction is not null)
                {
                    if (!_committed) await transaction.RollbackAsync();
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                local?.Release();
            }
        }
    }
}
