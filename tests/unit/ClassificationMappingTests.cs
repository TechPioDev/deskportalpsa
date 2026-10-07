using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Mapping;
using Desk.Application.Sync;
using Desk.Domain.Enums;
using Desk.Domain.Mapping;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Persistence;
using Desk.Infrastructure.Sync;
using Desk.Infrastructure.Tenancy;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// A PSA's classification becomes the portal's category, work type and subcategory only by a rule
/// somebody wrote for that connection.
///
/// What these hold: that a classification no rule names is unmapped and is given nothing; that the
/// PSA's own words stay on the ticket whatever the rules say; that rules are one connection's and
/// one organization's; that a preview changes nothing; that "apply" does to the tickets already
/// here exactly what the sync would on reading them again, and no more; and that every change is
/// in the audit log. On a SQL translator, because the figures are grouped counts and the rewrite
/// compares columns that may be null.
/// </summary>
public sealed class ClassificationMappingTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Elsewhere = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new();
    private readonly TenantContext _tenant = new();
    private readonly DeskDbContext _db;
    private readonly DeskDbContext _platform;
    private Guid _main, _second, _theirs, _acme;
    private int _n;

    public ClassificationMappingTests()
    {
        _connection.Open();
        _tenant.SetTenant(Org);
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, _tenant, _clock);
        _db.Database.EnsureCreated();
        // The scheduled sync's view of the same database: every organization's rows, no filter.
        var platform = new TenantContext();
        platform.SetPlatformScope();
        _platform = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options, platform, _clock);
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        PsaConnection Of(Guid org, string name) => new()
        {
            MspOrganizationId = org, Name = name, Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.example/",
            CredentialSecretRef = "mem://" + name, Status = ConnectionStatus.Healthy, IsEnabled = true,
        };
        var (main, second, theirs) = (Of(Org, "Main"), Of(Org, "Second account"), Of(Elsewhere, "Somebody else's"));
        (_main, _second, _theirs) = (main.Id, second.Id, theirs.Id);
        var acme = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = main.Id, Name = "Acme", ExternalCompanyId = "1" };
        _acme = acme.Id;
        _platform.AddRange(
            new MspOrganization { Id = Org, Name = "TechPio", Slug = "techpio", TimeZone = "UTC" },
            new MspOrganization { Id = Elsewhere, Name = "Another MSP", Slug = "another", TimeZone = "UTC" },
            main, second, theirs, acme,
            new ClientCompany { MspOrganizationId = Org, PsaConnectionId = second.Id, Name = "Acme", ExternalCompanyId = "1" },
            new ClientCompany { MspOrganizationId = Elsewhere, PsaConnectionId = theirs.Id, Name = "Initech", ExternalCompanyId = "1" });
        _platform.SaveChanges();
        _platform.ChangeTracker.Clear();
    }

    private ClassificationMappingService Service()
        => new(_db, new InOrder(new AuditWriter(_db, new TestCurrentUser(Org), _tenant, _clock), _clock), new MappingEngine(), _clock);

    /// <summary>The audit log, with the clock moved on after each entry so that the order they were written in can be read back.</summary>
    private sealed class InOrder(IAuditWriter inner, TestClock clock) : IAuditWriter
    {
        public async Task WriteAsync(string action, string entityType, string? entityId, object? detail = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(action, entityType, entityId, detail, ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The sync as the worker runs it: under platform scope, one instance for a run.</summary>
    private TicketSyncService Sync()
    {
        // A run starts with nothing held from the run before, as the runner lets go of each page.
        _platform.ChangeTracker.Clear();
        return new(_platform, new MappingEngine(), new SyncEventStore(_platform, _clock), _clock, new RecordingActivity());
    }

    private UnifiedTicket Filed(string? type, string? issue, string? sub, string? category = "Standard", string? id = null) => new()
    {
        ExternalId = id ?? (++_n).ToString(), Title = $"Ticket {_n}", Status = "5", Priority = "1",
        RequesterExternalId = "1", RequesterName = "Acme", RequesterEmail = "a@acme.test",
        Category = category, TicketType = type, IssueType = issue, SubIssueType = sub,
    };

    private static ClassificationRuleDto Rule(string? type, string? issue, string? sub, string? category = null, string? workType = null, string? subcategory = null)
        => new(null, type, issue, sub, category, workType, subcategory);

    private static ClassificationMapping Stored(string type, string issue, string sub, string? category = null, string? workType = null, string? subcategory = null)
        => new() { TicketType = type, IssueType = issue, SubIssueType = sub, Category = category, WorkType = workType, Subcategory = subcategory };

    private async Task<Ticket> TicketAsync(string externalId, Guid? connection = null)
    {
        _db.ChangeTracker.Clear();
        return await _db.Tickets.AsNoTracking().SingleAsync(t => t.PsaConnectionId == (connection ?? _main) && t.ExternalTicketId == externalId);
    }

    private async Task<List<(string Action, string Detail)>> AuditedAsync()
        => (await _db.AuditLog.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Select(a => new { a.Action, a.DetailJson }).ToListAsync())
            .Where(a => a.Action.StartsWith("classification."))
            .Select(a => (a.Action, a.DetailJson ?? "")).ToList();

    // ------------------------------------------------------------------ what a rule means

    [Fact]
    public void A_rule_speaks_for_the_levels_it_names_and_for_nothing_else()
    {
        var rules = new[] { Stored("Incident", "Hardware", "", workType: "Break/fix") };

        ClassificationRules.Resolve(rules, "Incident", "Hardware", "Printer").WorkType.Should().Be("Break/fix", "the third level is left open by the rule");
        ClassificationRules.Resolve(rules, " incident ", "HARDWARE", null).WorkType.Should().Be("Break/fix", "the PSA's wording is compared without case or stray space");
        ClassificationRules.Resolve(rules, "Incident", "Software", "Printer").Should().Be(ClassificationResult.Unmapped);
        ClassificationRules.Resolve(rules, "Incident", null, null).Should().Be(ClassificationResult.Unmapped, "a rule that names a level does not speak for a ticket without one");
        ClassificationRules.Resolve(rules, "Service Request", "Hardware", null).Should().Be(ClassificationResult.Unmapped);
    }

    [Fact]
    public void Each_of_the_three_comes_from_the_most_exact_rule_that_gives_it()
    {
        var rules = new[]
        {
            Stored("Incident", "", "", category: "Support", workType: "Reactive"),
            Stored("Incident", "Hardware", "", workType: "Break/fix", subcategory: "Hardware"),
            Stored("Incident", "Hardware", "Printer", subcategory: "Printing"),
            Stored("", "", "Printer", subcategory: "Never: a rule naming all three is more exact than one naming the last"),
        };

        var printer = ClassificationRules.Resolve(rules, "Incident", "Hardware", "Printer");
        printer.Should().BeEquivalentTo(new { Matched = true, Category = "Support", WorkType = "Break/fix", Subcategory = "Printing" },
            "the narrow rules add what only they know and the broad one still says what kind of work it is");
        ClassificationRules.Resolve(rules, "Incident", "Hardware", "Laptop")
            .Should().BeEquivalentTo(new { Category = "Support", WorkType = "Break/fix", Subcategory = "Hardware" });
        ClassificationRules.Resolve(rules, "Incident", "Network", null)
            .Should().BeEquivalentTo(new { Category = "Support", WorkType = "Reactive", Subcategory = (string?)null });

        // Of two rules naming as many levels, the one naming the deeper level is the more exact.
        var tie = new[] { Stored("Incident", "", "", category: "By type"), Stored("", "", "Printer", category: "By item") };
        ClassificationRules.Resolve(tie, "Incident", "Hardware", "Printer").Category.Should().Be("By item");
    }

    [Fact]
    public void A_classification_no_rule_names_is_unmapped_and_a_rule_for_everything_is_not_obeyed()
    {
        ClassificationRules.Resolve([], "Incident", "Hardware", null).Should().Be(ClassificationResult.Unmapped);
        ClassificationRules.Resolve([Stored("Incident", "", "", category: "Support")], null, " ", "").Should().Be(ClassificationResult.Unmapped,
            "a ticket filed under nothing has nothing to map");
        // None can be saved (see below). One that reached the table some other way gives nothing.
        ClassificationRules.Resolve([Stored("", " ", "", category: "Everything else")], "Brand new type", null, null)
            .Should().Be(ClassificationResult.Unmapped, "an unknown value is never given a meaning by a rule nobody wrote for it");
    }

    // ------------------------------------------------------------------ saving rules

    [Fact]
    public async Task Everything_wrong_with_a_set_of_rules_is_said_at_once_and_none_of_it_is_saved()
    {
        var service = Service();
        var act = () => service.SaveAsync(_main,
        [
            Rule("Incident", "Hardware", null, workType: "Break/fix"),
            Rule(null, " ", "", category: "Everything else"),
            Rule("Incident", null, null),
            Rule(" incident ", "HARDWARE", null, category: "Support"),
            Rule(new string('x', 201), null, null, category: "Long"),
        ]);

        var refused = (await act.Should().ThrowAsync<ValidationFailedException>()).Which.Message;
        refused.Should().Contain("Rule 2 names none of the PSA's levels").And.Contain("there is no rule for everything else")
            .And.Contain("Rule 3 gives no category, work type or subcategory")
            .And.Contain("Rule 4 is for the same classification as rule 1")
            .And.Contain("Rule 5: the first level is longer than 200 characters");
        (await _db.ClassificationMappings.CountAsync()).Should().Be(0, "the good rule is not saved beside the refusal of the others");
        (await AuditedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_replaces_the_connections_rules_and_the_audit_says_what_was_added_changed_and_removed()
    {
        var service = Service();
        var first = await service.SaveAsync(_main,
        [
            Rule("Incident", "Hardware", null, workType: "Break/fix"),
            Rule("Service Request", null, null, category: "Requests"),
        ]);
        first.Rules.Should().HaveCount(2);
        var kept = first.Rules.Single(r => r.TicketType == "Incident").Id;

        var second = await service.SaveAsync(_main,
        [
            Rule("incident", "hardware", null, workType: "Break/fix", subcategory: "Hardware"),
            Rule("Change", null, null, workType: "Project"),
        ]);

        second.Rules.Select(r => (r.TicketType, r.IssueType, r.WorkType, r.Subcategory)).Should().BeEquivalentTo(new[]
        {
            ("incident", (string?)"hardware", (string?)"Break/fix", (string?)"Hardware"),
            ("Change", null, "Project", null),
        });
        second.Rules.Single(r => r.TicketType == "incident").Id.Should().Be(kept, "the same classification is the same rule, changed");
        (await _platform.ClassificationMappings.AsNoTracking().ToListAsync())
            .Should().OnlyContain(r => r.MspOrganizationId == Org && r.PsaConnectionId == _main);

        var audited = await AuditedAsync();
        audited.Select(a => a.Action).Should().Equal("classification.mapping.changed", "classification.mapping.changed");
        audited[1].Detail.Should().Contain("Change / any / any").And.Contain("work type Project")
            .And.Contain("Service Request / any / any").And.Contain("category Requests")
            .And.Contain("subcategory Hardware");

        // Saved again as it is: nothing changed, so nothing is written to the audit log.
        await service.SaveAsync(_main, second.Rules);
        (await AuditedAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Another_organizations_connection_is_not_found_and_its_rules_are_not_touched()
    {
        _platform.ClassificationMappings.Add(new ClassificationMapping
        {
            MspOrganizationId = Elsewhere, PsaConnectionId = _theirs, TicketType = "Incident", Category = "Theirs",
        });
        await _platform.SaveChangesAsync();
        var service = Service();

        await FluentActions.Awaiting(() => service.GetAsync(_theirs)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.SaveAsync(_theirs, [Rule("Incident", null, null, category: "Mine now")])).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.PreviewAsync(_theirs, [])).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.ApplyAsync(_theirs)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.GetAsync(Guid.NewGuid())).Should().ThrowAsync<NotFoundException>();

        (await _platform.ClassificationMappings.AsNoTracking().SingleAsync()).Category.Should().Be("Theirs");
        (await service.GetAsync(_main)).Rules.Should().BeEmpty("their rule is not listed among ours either");
        (await AuditedAsync()).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ as a ticket arrives

    [Fact]
    public async Task A_ticket_arrives_with_what_the_rules_say_and_keeps_the_PSAs_own_words()
    {
        await Service().SaveAsync(_main,
        [
            Rule("Incident", null, null, category: "Support"),
            Rule("Incident", "Hardware", null, workType: "Break/fix", subcategory: "Hardware"),
        ]);
        var sync = Sync();

        (await sync.UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", "Printer", id: "1"), [])).Should().Be(TicketSyncOutcome.Created);
        (await sync.UpsertFromProviderAsync(_main, Filed("Service Request", "Access", null, id: "2"), [])).Should().Be(TicketSyncOutcome.Created);
        (await sync.UpsertFromProviderAsync(_main, Filed(null, null, null, id: "3"), [])).Should().Be(TicketSyncOutcome.Created);

        var mapped = await TicketAsync("1");
        (mapped.PortalCategory, mapped.PortalWorkType, mapped.PortalSubcategory).Should().Be(("Support", "Break/fix", "Hardware"));
        (mapped.PsaTicketType, mapped.PsaIssueType, mapped.PsaSubIssueType, mapped.PsaCategory).Should().Be(("Incident", "Hardware", "Printer", "Standard"),
            "what the PSA sent is kept beside what the rules made of it");

        // UNMAPPED: no rule names it. Nothing is carried across in a rule's place. The category is
        // what the category field's own mapping makes of the PSA's category, as it always was.
        var unmapped = await TicketAsync("2");
        (unmapped.PortalWorkType, unmapped.PortalSubcategory).Should().Be(((string?)null, (string?)null));
        unmapped.PortalCategory.Should().Be("Standard");
        (unmapped.PsaTicketType, unmapped.PsaIssueType).Should().Be(("Service Request", "Access"));

        var bare = await TicketAsync("3");
        (bare.PortalCategory, bare.PortalWorkType, bare.PortalSubcategory).Should().Be(("Standard", null, null));

        // Read again as they were: nothing to do. Idempotent with rules as without.
        (await sync.UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", "Printer", id: "1"), [])).Should().Be(TicketSyncOutcome.SkippedUnchanged);
        (await sync.UpsertFromProviderAsync(_main, Filed("Service Request", "Access", null, id: "2"), [])).Should().Be(TicketSyncOutcome.SkippedUnchanged);
    }

    [Fact]
    public async Task A_rule_written_after_a_ticket_arrived_reaches_it_the_next_time_it_is_read()
    {
        (await Sync().UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", null, id: "1"), [])).Should().Be(TicketSyncOutcome.Created);
        (await Sync().UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", null, id: "1"), [])).Should().Be(TicketSyncOutcome.SkippedUnchanged);

        await Service().SaveAsync(_main, [Rule("Incident", "Hardware", null, workType: "Break/fix")]);
        // The next run: the PSA sends the ticket exactly as before, and it is not "unchanged".
        (await Sync().UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", null, id: "1"), [])).Should().Be(TicketSyncOutcome.Updated);
        (await TicketAsync("1")).PortalWorkType.Should().Be("Break/fix");

        await Service().SaveAsync(_main, []);
        (await Sync().UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", null, id: "1"), [])).Should().Be(TicketSyncOutcome.Updated);
        (await TicketAsync("1")).PortalWorkType.Should().BeNull("the rule is gone, and nothing of it is left on the ticket");
    }

    [Fact]
    public async Task Rules_are_one_connections_and_say_nothing_about_another_account_of_the_same_PSA()
    {
        await Service().SaveAsync(_main, [Rule("Incident", "Hardware", null, category: "Support", workType: "Break/fix")]);
        var sync = Sync();
        await sync.UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", null, id: "1"), []);
        await sync.UpsertFromProviderAsync(_second, Filed("Incident", "Hardware", null, id: "1"), []);
        await sync.UpsertFromProviderAsync(_theirs, Filed("Incident", "Hardware", null, id: "1"), []);

        (await TicketAsync("1", _main)).PortalWorkType.Should().Be("Break/fix");
        var other = await TicketAsync("1", _second);
        (other.PortalCategory, other.PortalWorkType).Should().Be(("Standard", null), "the same words in another account are that account's to map");
        (await _platform.Tickets.AsNoTracking().SingleAsync(t => t.PsaConnectionId == _theirs)).PortalWorkType.Should().BeNull();

        var second = await Service().GetAsync(_second);
        second.Rules.Should().BeEmpty();
        second.Seen.Should().ContainSingle().Which.Mapped.Should().BeFalse();
        second.Health.Should().BeEquivalentTo(new { Rules = 0, ClassifiedTickets = 1, UnmappedTickets = 1, Coverage = 0.0 });
    }

    // ------------------------------------------------------------------ health, preview, apply

    private async Task SeedTicketsAsync()
    {
        var sync = Sync();
        foreach (var (type, issue, sub, count) in new[]
                 {
                     ("Incident", "Hardware", "Printer", 3), ("Incident", "Hardware", (string?)null, 2),
                     ("Incident", "Software", null, 4), ("Service Request", "Access", null, 5),
                 })
            for (var i = 0; i < count; i++)
                await sync.UpsertFromProviderAsync(_main, Filed(type, issue, sub), []);
        // Filed under nothing by the PSA.
        await sync.UpsertFromProviderAsync(_main, Filed(null, null, null), []);
        // Raised in the portal and not read back yet: the category is what the person raising it chose.
        _db.Tickets.Add(new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = _main, Provider = ProviderType.AutotaskPsa, ClientCompanyId = _acme,
            CorrelationId = Guid.NewGuid(), Title = "Raised here", RequesterName = "Acme", RequesterEmail = "a@acme.test",
            PortalCategory = "Chosen by the person raising it",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task What_tickets_are_filed_under_is_listed_unmapped_first_and_counted_against_the_rules()
    {
        await SeedTicketsAsync();
        var service = Service();

        var none = await service.GetAsync(_main);
        none.Health.Should().Be(new ClassificationHealthDto(Rules: 0, ClassifiedTickets: 14, UnmappedTickets: 14,
            Classifications: 4, MappedClassifications: 0, Coverage: 0, RulesMatchingNothing: 0, TicketsOutOfStep: 0));
        none.Seen.Select(s => (s.TicketType, s.IssueType, s.SubIssueType, s.Tickets, s.Mapped)).Should().Equal(
            ("Service Request", "Access", null, 5, false), ("Incident", "Software", null, 4, false),
            ("Incident", "Hardware", "Printer", 3, false), ("Incident", "Hardware", null, 2, false));

        var some = await service.SaveAsync(_main,
        [
            Rule("Incident", "Hardware", null, category: "Support", workType: "Break/fix"),
            Rule("Project", null, null, workType: "Project"),
        ]);

        some.Health.Should().Be(new ClassificationHealthDto(Rules: 2, ClassifiedTickets: 14, UnmappedTickets: 9,
            Classifications: 4, MappedClassifications: 2, Coverage: 35.7, RulesMatchingNothing: 1, TicketsOutOfStep: 5),
            "five tickets are under a rule now and still hold what they arrived with; saving does not touch them");
        some.Seen.Select(s => (s.TicketType, s.IssueType, s.SubIssueType, s.Mapped, s.Category, s.WorkType)).Should().Equal(
            ("Service Request", "Access", null, false, null, null), ("Incident", "Software", null, false, null, null),
            ("Incident", "Hardware", "Printer", true, "Support", "Break/fix"), ("Incident", "Hardware", null, true, "Support", "Break/fix"));
        some.WorkTypes.Should().Equal("Break/fix", "Project");
        some.Categories.Should().Contain(["Support", "Standard"]);
    }

    [Fact]
    public async Task A_preview_says_what_rules_would_do_and_does_none_of_it()
    {
        await SeedTicketsAsync();
        var service = Service();
        var before = await _db.Tickets.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.PortalCategory, t.PortalWorkType, t.PortalSubcategory, t.Version }).ToListAsync();

        var preview = await service.PreviewAsync(_main,
        [
            Rule("Incident", "Hardware", null, category: "Support", workType: "Break/fix"),
            Rule("Incident", "Hardware", "Printer", subcategory: "Printing"),
        ]);

        preview.TicketsThatWouldChange.Should().Be(5);
        preview.Health.Should().BeEquivalentTo(new { Rules = 2, UnmappedTickets = 9, MappedClassifications = 2, TicketsOutOfStep = 5 });
        preview.Sample.Should().HaveCount(5).And.OnlyContain(t => t.CategoryNow == "Standard" && t.CategoryThen == "Support" && t.WorkTypeNow == null && t.WorkTypeThen == "Break/fix");
        preview.Sample.Where(t => t.SubIssueType == "Printer").Should().HaveCount(3).And.OnlyContain(t => t.SubcategoryThen == "Printing");

        (await _db.ClassificationMappings.CountAsync()).Should().Be(0, "a preview saves no rule");
        (await _db.Tickets.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.PortalCategory, t.PortalWorkType, t.PortalSubcategory, t.Version }).ToListAsync())
            .Should().Equal(before, "and changes no ticket");
        (await AuditedAsync()).Should().BeEmpty();

        // A preview refuses what saving would refuse, in the same words.
        await FluentActions.Awaiting(() => service.PreviewAsync(_main, [Rule(null, null, null, category: "Everything")]))
            .Should().ThrowAsync<ValidationFailedException>().WithMessage("*there is no rule for everything else*");
    }

    [Fact]
    public async Task Apply_gives_the_tickets_already_here_what_the_sync_would_and_leaves_the_rest_alone()
    {
        await SeedTicketsAsync();
        var service = Service();
        await service.SaveAsync(_main,
        [
            Rule("Incident", "Hardware", null, category: "Support", workType: "Break/fix"),
            Rule("Incident", "Hardware", "Printer", subcategory: "Printing"),
        ]);

        var applied = await service.ApplyAsync(_main);

        applied.TicketsChanged.Should().Be(5);
        applied.Changes.Should().BeEquivalentTo(
            "Incident / Hardware / Printer → Support, Break/fix, Printing (3)", "Incident / Hardware / – → Support, Break/fix, – (2)");
        var after = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _main).ToListAsync();
        after.Where(t => t.PsaIssueType == "Hardware").Should().HaveCount(5)
            .And.OnlyContain(t => t.PortalCategory == "Support" && t.PortalWorkType == "Break/fix" && t.PsaCategory == "Standard" && t.PsaTicketType == "Incident");
        after.Where(t => t.PsaSubIssueType == "Printer").Should().OnlyContain(t => t.PortalSubcategory == "Printing");
        after.Where(t => t.PsaIssueType is "Software" or "Access").Should().HaveCount(9)
            .And.OnlyContain(t => t.PortalCategory == "Standard" && t.PortalWorkType == null && t.PortalSubcategory == null, "no rule names them: unmapped, and left as they were");
        after.Single(t => t.Title == "Raised here").PortalCategory.Should().Be("Chosen by the person raising it",
            "a ticket the PSA has filed under nothing is not this page's to rewrite");

        (await service.GetAsync(_main)).Health.TicketsOutOfStep.Should().Be(0);
        (await service.ApplyAsync(_main)).TicketsChanged.Should().Be(0, "done once, there is nothing left to do");

        // And the sync, reading one of them again, agrees with what apply wrote: nothing changes hands twice.
        var one = after.First(t => t.PsaSubIssueType == "Printer");
        await Sync().UpsertFromProviderAsync(_main, Filed("Incident", "Hardware", "Printer", id: one.ExternalTicketId), []);
        var reread = await TicketAsync(one.ExternalTicketId!);
        (reread.PortalCategory, reread.PortalWorkType, reread.PortalSubcategory).Should().Be(("Support", "Break/fix", "Printing"));

        var audited = await AuditedAsync();
        audited.Select(a => a.Action).Should().Equal("classification.mapping.changed", "classification.mapping.applied", "classification.mapping.applied");
        audited[1].Detail.Should().Contain("\"tickets\":5");
    }

    [Fact]
    public async Task Taking_a_rule_away_and_applying_puts_the_tickets_back_as_they_would_arrive_without_it()
    {
        await SeedTicketsAsync();
        // The category field has a mapping of its own: the PSA's "Standard" is "General" here.
        _db.FieldMappings.Add(new FieldMapping
        {
            MspOrganizationId = Org, PsaConnectionId = _main, Provider = ProviderType.AutotaskPsa,
            Scope = MappingScope.ConnectionOverride, Direction = MappingDirection.Bidirectional,
            PortalField = "category", ExternalField = "category", PortalValue = "General", ExternalValue = "Standard",
        });
        await _db.SaveChangesAsync();
        var service = Service();
        await service.SaveAsync(_main, [Rule("Incident", "Hardware", null, category: "Support", workType: "Break/fix")]);
        await service.ApplyAsync(_main);

        await service.SaveAsync(_main, [Rule("Incident", "Hardware", null, workType: "Break/fix")]);
        (await service.GetAsync(_main)).Health.TicketsOutOfStep.Should().BeGreaterThan(0);
        await service.ApplyAsync(_main);

        var hardware = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _main && t.PsaIssueType == "Hardware").ToListAsync();
        hardware.Should().HaveCount(5).And.OnlyContain(t => t.PortalCategory == "General" && t.PortalWorkType == "Break/fix",
            "the rule no longer gives a category, so the category is the category field's own mapping again");

        await service.SaveAsync(_main, []);
        var cleared = await service.ApplyAsync(_main);
        cleared.Changes.Should().Contain(c => c.Contains("→ unmapped"));
        (await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _main && t.PsaIssueType == "Hardware").ToListAsync())
            .Should().OnlyContain(t => t.PortalWorkType == null && t.PortalSubcategory == null && t.PsaIssueType == "Hardware");
    }

    [Fact]
    public async Task Apply_does_a_connection_of_more_tickets_than_one_statement_takes()
    {
        var company = await _platform.ClientCompanies.AsNoTracking().Where(c => c.PsaConnectionId == _main).Select(c => c.Id).SingleAsync();
        _platform.Tickets.AddRange(Enumerable.Range(0, 1203).Select(i => new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = _main, Provider = ProviderType.AutotaskPsa, ClientCompanyId = company,
            ExternalTicketId = "v" + i, CorrelationId = Guid.NewGuid(), Title = "Volume " + i, RequesterName = "Acme", RequesterEmail = "a@acme.test",
            PsaTicketType = "Incident", PsaIssueType = i % 3 == 0 ? "Hardware" : "Software", PsaCategory = "Standard", PortalCategory = "Standard",
        }));
        await _platform.SaveChangesAsync();
        _platform.ChangeTracker.Clear();
        var service = Service();
        await service.SaveAsync(_main, [Rule("Incident", null, null, workType: "Reactive")]);

        var versions = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _main).Select(t => new { t.Id, t.Version }).ToDictionaryAsync(t => t.Id, t => t.Version);
        _clock.Advance(TimeSpan.FromHours(1));
        var at = _clock.GetUtcNow();

        (await service.ApplyAsync(_main)).TicketsChanged.Should().Be(1203);
        var after = await _db.Tickets.AsNoTracking().Where(t => t.PsaConnectionId == _main).ToListAsync();
        after.Should().HaveCount(1203).And.OnlyContain(t => t.PortalWorkType == "Reactive" && t.PortalCategory == "Standard")
            .And.OnlyContain(t => t.Version == versions[t.Id] + 1, "anything holding a ticket from before knows it has changed")
            .And.OnlyContain(t => t.UpdatedAt == at);
        _db.ChangeTracker.Entries<Ticket>().Should().BeEmpty("no ticket is loaded to be rewritten");
    }

    [Fact]
    public async Task The_connections_mapping_health_counts_classification_only_once_there_are_rules()
    {
        await SeedTicketsAsync();
        // Everything else about the connection's mapping is in order: every status and priority
        // the tickets hold is mapped, and every portal status has something to be sent as.
        FieldMapping Field(string field, string portal, string external) => new()
        {
            MspOrganizationId = Org, PsaConnectionId = _main, Provider = ProviderType.AutotaskPsa,
            Scope = MappingScope.ConnectionOverride, Direction = MappingDirection.Bidirectional,
            PortalField = field, ExternalField = field, PortalValue = portal, ExternalValue = external,
        };
        _db.FieldMappings.AddRange(PortalVocabulary.Statuses.Select((status, i) => Field("status", status, i == 0 ? "5" : status)));
        _db.FieldMappings.Add(Field("priority", "NORMAL", "1"));
        await _db.SaveChangesAsync();
        var admin = new ConnectionAdminService(_db, new Desk.Infrastructure.Secrets.InMemorySecretStore(),
            new AuditWriter(_db, new TestCurrentUser(Org), _tenant, _clock), new NoPsa(), new ConnectionFieldCache(),
            new Desk.Infrastructure.Attachments.InMemoryObjectStorage(new Desk.Infrastructure.Attachments.AttachmentStorageOptions(), _clock), _clock);

        // Writing no rules is a choice, and is not held against the connection.
        var without = await admin.MappingHealthAsync(_main);
        without.Classification.Should().BeEquivalentTo(new { Rules = 0, ClassifiedTickets = 14, UnmappedTickets = 14 });
        without.Level.Should().Be(ConnectionAdminService.Pass);

        // Having written some, what they do not reach is something left to do, and no more than that.
        await Service().SaveAsync(_main, [Rule("Incident", "Hardware", null, workType: "Break/fix")]);
        var with = await admin.MappingHealthAsync(_main);
        with.Classification.Should().BeEquivalentTo(new { Rules = 1, UnmappedTickets = 9, TicketsOutOfStep = 5 });
        with.Level.Should().Be(ConnectionAdminService.Optional);

        // Every classification named and the tickets brought into step: nothing is left.
        await Service().SaveAsync(_main,
        [
            Rule("Incident", "Hardware", null, workType: "Break/fix"), Rule("Incident", "Software", null, workType: "Break/fix"),
            Rule("Service Request", null, null, workType: "Request"),
        ]);
        await Service().ApplyAsync(_main);
        var done = await admin.MappingHealthAsync(_main);
        done.Classification.Should().BeEquivalentTo(new { UnmappedTickets = 0, TicketsOutOfStep = 0, Coverage = 100.0 });
        done.Level.Should().Be(ConnectionAdminService.Pass);
    }

    /// <summary>The PSA cannot be reached: mapping health is then counted from the tickets alone.</summary>
    private sealed class NoPsa : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<Desk.PsaCore.Contracts.IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default)
            => throw new ConnectorException(ConnectorFailureKind.Timeout, "The PSA did not answer.");
    }
}
