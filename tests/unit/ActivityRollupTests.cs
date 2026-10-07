using Desk.Domain.Analytics;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Analytics;
using Desk.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Rolling the activity log into daily facts, and keeping the raw log bounded.
///
/// The behaviours worth pinning are the ones that only show up over time: that a late-arriving
/// event corrects the day it belongs to rather than the day it arrived, that recomputing twice does
/// not double anything, and that expiry never runs ahead of the rollup that preserves the history.
/// </summary>
public class ActivityRollupTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();

    private static async Task<DeskDbContext> SeedAsync()
    {
        var db = TestDbContextFactory.ForPlatform(Guid.NewGuid().ToString());
        db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "AT", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        db.ClientCompanies.Add(new ClientCompany
        { Id = Company, MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" });
        await db.SaveChangesAsync();
        return db;
    }

    private static void AddEvent(DeskDbContext db, DateTimeOffset when, ActivitySource source,
        string? actorExternal = null, Guid? actorUser = null, int? duration = null)
        => db.ActivityEvents.Add(new ActivityEvent
        {
            MspOrganizationId = Org, OccurredAt = when, Source = source, Kind = ActivityKind.NoteAdded,
            ActorExternalId = actorExternal, ActorUserId = actorUser,
            ClientCompanyId = Company, DurationSeconds = duration,
        });

    [Fact]
    public async Task Events_roll_up_per_day_source_and_actor()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var db = await SeedAsync();
        AddEvent(db, now.AddHours(-1), ActivitySource.Portal, actorExternal: "R1");
        AddEvent(db, now.AddHours(-2), ActivitySource.Portal, actorExternal: "R1");
        AddEvent(db, now.AddHours(-3), ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        var facts = await db.ActivityDailyFacts.IgnoreQueryFilters().ToListAsync();
        facts.Should().HaveCount(2, "same day and actor, but two different sources");
        facts.Single(f => f.Source == ActivitySource.Portal).EventCount.Should().Be(2);
        facts.Single(f => f.Source == ActivitySource.Psa).EventCount.Should().Be(1);
    }

    [Fact]
    public async Task Running_twice_does_not_double_the_facts()
    {
        // The pass recomputes rather than appends, so it has to be safe to run on any schedule —
        // including twice in a row after a retry.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        AddEvent(db, clock.GetUtcNow().AddHours(-1), ActivitySource.Portal, actorExternal: "R1");
        await db.SaveChangesAsync();
        var svc = new ActivityRollupService(db, clock);

        await svc.RunAsync();
        await svc.RunAsync();

        var facts = await db.ActivityDailyFacts.IgnoreQueryFilters().ToListAsync();
        facts.Should().ContainSingle();
        facts[0].EventCount.Should().Be(1);
    }

    [Fact]
    public async Task A_late_arriving_event_corrects_the_day_it_belongs_to()
    {
        // PSA data arrives late: a closure observed today can carry a timestamp from days ago. A
        // rollup that only appended would credit it to the wrong day permanently.
        var clock = new TestClock();
        var threeDaysAgo = clock.GetUtcNow().AddDays(-3);
        await using var db = await SeedAsync();
        AddEvent(db, threeDaysAgo, ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();
        var svc = new ActivityRollupService(db, clock);
        await svc.RunAsync();

        // A second event for that same past day turns up afterwards.
        AddEvent(db, threeDaysAgo.AddHours(1), ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();
        await svc.RunAsync();

        var day = DateOnly.FromDateTime(threeDaysAgo.UtcDateTime.Date);
        var fact = await db.ActivityDailyFacts.IgnoreQueryFilters().SingleAsync(f => f.Day == day);
        fact.EventCount.Should().Be(2, "the recompute window rebuilds the day, it does not add to it");
    }

    [Fact]
    public async Task A_portal_event_is_attributed_through_the_psa_identity()
    {
        // Both halves must aggregate on the same axis, or PSA and portal figures cannot be compared
        // for the same person.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var user = Guid.NewGuid();
        db.AppUsers.Add(new AppUser { Id = user, MspOrganizationId = Org, Email = "h@t.test", DisplayName = "Harpal" });
        db.UserPsaIdentities.Add(new UserPsaIdentity
        {
            MspOrganizationId = Org, AppUserId = user, PsaConnectionId = Conn,
            ExternalTechnicianId = "R1", ExternalTechnicianName = "Harpal Singh",
        });
        AddEvent(db, clock.GetUtcNow().AddHours(-1), ActivitySource.Portal, actorUser: user);
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        (await db.ActivityDailyFacts.IgnoreQueryFilters().SingleAsync())
            .ActorExternalId.Should().Be("R1");
    }

    private static Guid AddPerson(DeskDbContext db, Guid org, Guid connection, string login)
    {
        var user = Guid.NewGuid();
        db.AppUsers.Add(new AppUser { Id = user, MspOrganizationId = org, Email = $"{user:N}@t.test", DisplayName = "P" });
        db.UserPsaIdentities.Add(new UserPsaIdentity
        {
            MspOrganizationId = org, AppUserId = user, PsaConnectionId = connection, ExternalTechnicianId = login,
        });
        return user;
    }

    [Fact]
    public async Task A_PSA_id_never_resolves_to_a_person_in_another_organization()
    {
        // One pass covers every tenant. Two tenants on the same PSA number their people the same
        // way, so the id alone used to resolve to whichever tenant's user was read first - and that
        // user's id was written into the other tenant's facts.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var otherOrg = Guid.NewGuid();
        var otherConn = Guid.NewGuid();
        var theirs = AddPerson(db, otherOrg, otherConn, "5");
        var ours = AddPerson(db, Org, Conn, "5");
        db.ActivityEvents.Add(new ActivityEvent
        {
            MspOrganizationId = Org, OccurredAt = clock.GetUtcNow().AddHours(-1), Source = ActivitySource.Psa,
            Kind = ActivityKind.NoteAdded, ActorExternalId = "5", PsaConnectionId = Conn, ClientCompanyId = Company,
        });
        db.ActivityEvents.Add(new ActivityEvent
        {
            MspOrganizationId = otherOrg, OccurredAt = clock.GetUtcNow().AddHours(-1), Source = ActivitySource.Psa,
            Kind = ActivityKind.NoteAdded, ActorExternalId = "5", PsaConnectionId = otherConn,
        });
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        var facts = await db.ActivityDailyFacts.IgnoreQueryFilters().ToListAsync();
        facts.Single(f => f.MspOrganizationId == Org).ActorAppUserId.Should().Be(ours);
        facts.Single(f => f.MspOrganizationId == otherOrg).ActorAppUserId.Should().Be(theirs);
    }

    [Fact]
    public async Task The_same_PSA_id_on_two_accounts_is_two_people()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        var secondAccount = Guid.NewGuid();
        var onFirst = AddPerson(db, Org, Conn, "5");
        var onSecond = AddPerson(db, Org, secondAccount, "5");
        void Observed(Guid? connection, int hoursAgo) => db.ActivityEvents.Add(new ActivityEvent
        {
            MspOrganizationId = Org, OccurredAt = clock.GetUtcNow().AddHours(-hoursAgo), Source = ActivitySource.Psa,
            Kind = ActivityKind.NoteAdded, ActorExternalId = "5", PsaConnectionId = connection, ClientCompanyId = Company,
        });
        Observed(Conn, 1);
        Observed(secondAccount, 2);
        Observed(null, 3);
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        var people = await db.ActivityDailyFacts.IgnoreQueryFilters().Select(f => f.ActorAppUserId).ToListAsync();
        // The event that names no account could be either of them, so it is credited to nobody
        // rather than to whichever was read first.
        people.Should().BeEquivalentTo(new Guid?[] { onFirst, onSecond, null });
    }

    [Fact]
    public async Task An_unmapped_actor_stays_null_rather_than_being_guessed_at()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        AddEvent(db, clock.GetUtcNow().AddHours(-1), ActivitySource.Portal, actorUser: Guid.NewGuid());
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        (await db.ActivityDailyFacts.IgnoreQueryFilters().SingleAsync())
            .ActorExternalId.Should().BeNull();
    }

    [Fact]
    public async Task An_event_older_than_the_rolling_window_still_becomes_a_fact()
    {
        // How the facts table sat empty while the log held 68 events. The pass only ever rebuilt the
        // last seven days, and PSA events are dated when they HAPPENED, not when they arrived — so a
        // closure imported today carrying last month's timestamp landed outside the window on its
        // very first pass and was never aggregated at all. Not an edge case: widening an import
        // brings in months of history at once, and every day of it lands this way.
        var clock = new TestClock();
        var longAgo = clock.GetUtcNow().AddDays(-60);
        await using var db = await SeedAsync();
        AddEvent(db, longAgo, ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        var day = DateOnly.FromDateTime(longAgo.UtcDateTime.Date);
        (await db.ActivityDailyFacts.IgnoreQueryFilters().SingleAsync(f => f.Day == day))
            .EventCount.Should().Be(1);
    }

    [Fact]
    public async Task A_day_already_rolled_up_is_left_alone_rather_than_rebuilt_forever()
    {
        // The backfill has to settle. If an old day were rebuilt on every pass, each hourly run
        // would rewrite the whole history — and the second pass would have nothing new to say.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        AddEvent(db, clock.GetUtcNow().AddDays(-60), ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();
        var svc = new ActivityRollupService(db, clock);

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        first.FactsWritten.Should().Be(1);
        second.DaysProcessed.Should().Be(RollingWindowDays,
            "once the old day has facts, only the rolling window is rebuilt");
        (await db.ActivityDailyFacts.IgnoreQueryFilters().CountAsync())
            .Should().Be(1, "and the fact it already wrote is still there, exactly once");
    }

    /// <summary>Today plus the seven days behind it.</summary>
    private const int RollingWindowDays = 8;

    [Fact]
    public async Task Backfilling_an_old_day_does_not_disturb_the_days_around_it()
    {
        // A rebuild is delete-then-insert, so the day set it deletes has to be exactly the day set
        // it rebuilds. Getting that wrong would quietly drop history either side of the gap.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        // INSIDE the span being read (-60 to today) but not itself being rebuilt: it has no events
        // and is not in the rolling window. Placed outside that span the database filter alone would
        // protect it, and this would pass while the day set was ignored entirely.
        var untouched = DateOnly.FromDateTime(clock.GetUtcNow().AddDays(-30).UtcDateTime.Date);
        db.ActivityDailyFacts.Add(new ActivityDailyFact
        {
            MspOrganizationId = Org, Day = untouched, Source = ActivitySource.Psa,
            ActorExternalId = "R1", EventCount = 42,
        });
        AddEvent(db, clock.GetUtcNow().AddDays(-60), ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        (await db.ActivityDailyFacts.IgnoreQueryFilters().SingleAsync(f => f.Day == untouched))
            .EventCount.Should().Be(42, "that day was not being rebuilt and keeps what it had");
    }

    [Fact]
    public async Task Raw_events_past_the_retention_horizon_are_expired()
    {
        var clock = new TestClock();
        await using var db = await SeedAsync();
        AddEvent(db, clock.GetUtcNow().AddDays(-400), ActivitySource.Psa, actorExternal: "R1"); // past 13 months
        AddEvent(db, clock.GetUtcNow().AddDays(-30), ActivitySource.Psa, actorExternal: "R1");  // inside
        await db.SaveChangesAsync();

        var result = await new ActivityRollupService(db, clock).RunAsync();

        result.RawEventsExpired.Should().Be(1);
        (await db.ActivityEvents.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Facts_outlive_the_raw_events_they_came_from()
    {
        // The point of the rollup: history survives expiry. A fact written a year ago must still be
        // there after the events behind it are gone.
        var clock = new TestClock();
        await using var db = await SeedAsync();
        db.ActivityDailyFacts.Add(new ActivityDailyFact
        {
            MspOrganizationId = Org,
            Day = DateOnly.FromDateTime(clock.GetUtcNow().AddDays(-400).UtcDateTime.Date),
            Source = ActivitySource.Psa, ActorExternalId = "R1", EventCount = 99,
        });
        AddEvent(db, clock.GetUtcNow().AddDays(-400), ActivitySource.Psa, actorExternal: "R1");
        await db.SaveChangesAsync();

        await new ActivityRollupService(db, clock).RunAsync();

        (await db.ActivityEvents.IgnoreQueryFilters().AnyAsync()).Should().BeFalse("the raw row expired");
        (await db.ActivityDailyFacts.IgnoreQueryFilters().AnyAsync(f => f.EventCount == 99))
            .Should().BeTrue("the fact is outside the recompute window and must survive");
    }
}
