using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Sync;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// What an administrator can see of a connection's sync, and the two things they may decide about
/// a record it could not get through. Under the caller's own tenant: another organization's
/// connection, run or failure is not found.
/// </summary>
public class SyncHealthTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private sealed record World(AdminHarness H, SyncHealthService Service, Guid Connection, Guid TheirConnection, Guid TheirFailure, string DbName);

    private static PsaConnection Connection(Guid org, string name) => new()
    {
        MspOrganizationId = org, Name = name, Provider = ProviderType.AutotaskPsa,
        ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
    };

    private static SyncFailure Failure(Guid org, Guid connection, string externalId, SyncFailureStatus status, int attempts, DateTimeOffset at) => new()
    {
        MspOrganizationId = org, PsaConnectionId = connection, Entity = SyncFailure.Ticket, ExternalId = externalId,
        Operation = SyncFailure.Operations.Notes, Category = "RateLimited", Message = "slow down", Attempts = attempts,
        FirstFailedAt = at, LastFailedAt = at, Status = status, NextAttemptAt = status == SyncFailureStatus.Pending ? at.AddHours(1) : null,
    };

    private static async Task<World> WorldAsync()
    {
        var name = Guid.NewGuid().ToString();
        var h = AdminHarness.Create(Org, name);
        var now = h.Clock.GetUtcNow();
        var (mine, theirs) = (Connection(Org, "Autotask"), Connection(Other, "Their Autotask"));
        var theirFailure = Failure(Other, theirs.Id, "900", SyncFailureStatus.NeedsReview, 6, now);
        await using (var seed = AdminHarness.Platform(name))
        {
            seed.AddRange(mine, theirs, theirFailure,
                new SyncCursor { MspOrganizationId = Org, PsaConnectionId = mine.Id, Entity = SyncCursor.Tickets, Watermark = now.AddMinutes(-7), Continuation = "400", ContinuationPages = 4 },
                new SyncRun { MspOrganizationId = Org, PsaConnectionId = mine.Id, Status = SyncRunStatus.Succeeded, Trigger = SyncRunTrigger.Scheduled, StartedAt = now.AddMinutes(-10), FinishedAt = now.AddMinutes(-9), Fetched = 12, Created = 2, FailedRecords = 1 },
                new SyncRun { MspOrganizationId = Org, PsaConnectionId = mine.Id, Status = SyncRunStatus.Running, Trigger = SyncRunTrigger.Manual, StartedAt = now.AddMinutes(-1), LeaseExpiresAt = now.AddMinutes(9), RequestedBy = "Asha Admin" },
                new SyncRun { MspOrganizationId = Other, PsaConnectionId = theirs.Id, Status = SyncRunStatus.Failed, StartedAt = now, Error = "their business" },
                Failure(Org, mine.Id, "101", SyncFailureStatus.Pending, 2, now.AddMinutes(-9)),
                Failure(Org, mine.Id, "102", SyncFailureStatus.NeedsReview, 6, now.AddMinutes(-30)),
                Failure(Org, mine.Id, "103", SyncFailureStatus.Resolved, 1, now.AddHours(-2)));
            await seed.SaveChangesAsync();
        }
        var service = new SyncHealthService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock);
        return new World(h, service, mine.Id, theirs.Id, theirFailure.Id, name);
    }

    [Fact]
    public async Task The_state_says_where_the_sync_is_and_what_it_owes()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;

        var state = await w.Service.StateAsync(w.Connection);

        (state.Watermark, state.ReadInProgress, state.PagesReadSoFar).Should().Be((w.H.Clock.GetUtcNow().AddMinutes(-7), true, 4));
        (state.Running, state.OpenFailures, state.NeedsReview).Should().Be((true, 2, 1));
        state.Runs.Select(r => (r.Status, r.Trigger, r.RequestedBy)).Should().Equal(
            ("Running", "Manual", "Asha Admin"), ("Succeeded", "Scheduled", null));
        state.Runs.Should().NotContain(r => r.Error == "their business");
    }

    [Fact]
    public async Task A_run_whose_lease_has_run_out_is_not_reported_as_running()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        w.H.Clock.Advance(TimeSpan.FromMinutes(30));

        (await w.Service.StateAsync(w.Connection)).Running.Should().BeFalse();
    }

    [Fact]
    public async Task Only_what_is_still_open_is_listed()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;

        var failures = await w.Service.FailuresAsync(w.Connection);

        failures.Select(f => (f.ExternalId, f.Status)).Should().Equal(("101", "Pending"), ("102", "NeedsReview"));
        failures.Should().OnlyContain(f => f.Operation == "notes" && f.Category == "RateLimited");
    }

    [Fact]
    public async Task Asking_for_a_retry_puts_the_record_at_the_front_of_the_next_run_once()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        var stuck = (await w.Service.FailuresAsync(w.Connection)).Single(f => f.ExternalId == "102");

        await w.Service.RetryAsync(w.Connection, stuck.Id);

        var row = await w.H.Db.SyncFailures.AsNoTracking().SingleAsync(f => f.Id == stuck.Id);
        (row.Status, row.NextAttemptAt).Should().Be((SyncFailureStatus.Pending, w.H.Clock.GetUtcNow()));
        row.Attempts.Should().Be(SyncFailureStore.MaxAttempts - 1, "one more try: if it fails again it comes straight back for review");
        (await w.H.Db.AuditLog.SingleAsync(a => a.Action == "sync.failure.retried")).EntityId.Should().Be(stuck.Id.ToString());
    }

    [Fact]
    public async Task A_dismissed_failure_is_kept_and_no_longer_tried()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;
        var pending = (await w.Service.FailuresAsync(w.Connection)).Single(f => f.ExternalId == "101");

        await w.Service.DismissAsync(w.Connection, pending.Id);

        var row = await w.H.Db.SyncFailures.AsNoTracking().SingleAsync(f => f.Id == pending.Id);
        (row.Status, row.NextAttemptAt).Should().Be((SyncFailureStatus.Dismissed, null));
        (await w.Service.FailuresAsync(w.Connection)).Select(f => f.ExternalId).Should().Equal("102");
        (await w.H.Db.AuditLog.CountAsync(a => a.Action == "sync.failure.dismissed")).Should().Be(1);
        // Already dealt with: there is nothing left to dismiss or retry.
        await ((Func<Task>)(() => w.Service.RetryAsync(w.Connection, pending.Id))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organizations_connection_runs_and_failures_are_not_found()
    {
        var w = await WorldAsync();
        await using var _ = w.H.Db;

        await ((Func<Task>)(() => w.Service.StateAsync(w.TheirConnection))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => w.Service.FailuresAsync(w.TheirConnection))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => w.Service.RetryAsync(w.TheirConnection, w.TheirFailure))).Should().ThrowAsync<NotFoundException>();
        // Their failure id under my own connection: still nothing.
        await ((Func<Task>)(() => w.Service.DismissAsync(w.Connection, w.TheirFailure))).Should().ThrowAsync<NotFoundException>();

        await using var all = AdminHarness.Platform(w.DbName);
        (await all.SyncFailures.AsNoTracking().SingleAsync(f => f.Id == w.TheirFailure)).Status.Should().Be(SyncFailureStatus.NeedsReview);
        (await w.H.Db.AuditLog.CountAsync()).Should().Be(0, "a refusal changes nothing and records nothing");
    }
}
