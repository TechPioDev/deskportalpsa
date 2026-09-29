using Desk.Application.Common;
using Desk.Application.Knowledge;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Domain.Enums;
using Desk.Domain.Knowledge;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Knowledge;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The knowledge base: who may read what (staff-only and drafts never reach a client; chosen-client
/// articles reach only those clients; each company reads only its own FAQ), suggestions while typing
/// a ticket, and the "this solved it" count that says whether the articles are earning their keep.
/// </summary>
public class KnowledgeBaseTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Globex = Guid.NewGuid();
    private static readonly Guid Tech = Guid.NewGuid();

    private sealed record Kit(AdminHarness H, KnowledgeBaseService Svc);

    private static async Task<Kit> BuildAsync()
    {
        var h = AdminHarness.Create(Org);
        var conn = new PsaConnection
        {
            MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa, ApiEndpoint = "https://at.test", CredentialSecretRef = "ref",
        };
        h.Db.PsaConnections.Add(conn);
        h.Db.ClientCompanies.AddRange(
            new ClientCompany { Id = Acme, MspOrganizationId = Org, PsaConnectionId = conn.Id, Name = "Acme", ExternalCompanyId = "1" },
            new ClientCompany { Id = Globex, MspOrganizationId = Org, PsaConnectionId = conn.Id, Name = "Globex", ExternalCompanyId = "2" });
        await h.Db.SaveChangesAsync();
        return new Kit(h, new KnowledgeBaseService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Clock));
    }

    private static ClientAccess As(Guid company) => new(Org, company, Guid.NewGuid(), IsCompanyAdministrator: false);

    private static Task<KbArticleDto> WriteAsync(Kit k, string title, string body, string audience, bool published = true,
        IReadOnlyList<Guid>? clients = null, string? category = null)
        => k.Svc.SaveAsync(null, new KbArticleInput(title, body, category, audience, clients, published), Tech, "Anika Sharma");

    private static async Task SeedAsync(Kit k)
    {
        await WriteAsync(k, "Connect to the VPN", "Open **FortiClient**, choose *Office*, sign in with your email.", "AllClients", category: "VPN");
        await WriteAsync(k, "Reset the Acme firewall", "Console cable, then `execute factoryreset`.", "Staff");
        await WriteAsync(k, "Acme's scanner to email", "Scans go to scans@acme.test.", "SelectedClients", clients: [Acme], category: "Printers");
        await WriteAsync(k, "New Wi-Fi password (draft)", "Not yet.", "AllClients", published: false);
        k.H.Db.FaqArticles.AddRange(
            new FaqArticle { MspOrganizationId = Org, ClientCompanyId = Acme, Question = "Where is the printer?", Answer = "Second floor.", Category = "Printers" },
            new FaqArticle { MspOrganizationId = Org, ClientCompanyId = Globex, Question = "Globex holiday rota", Answer = "Ask HR." });
        await k.H.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_client_reads_what_was_meant_for_them_and_nothing_else()
    {
        var k = await BuildAsync();
        await SeedAsync(k);

        (await k.Svc.HelpAsync(As(Acme), null)).Select(a => a.Title).Should().BeEquivalentTo(
            "Connect to the VPN", "Acme's scanner to email", "Where is the printer?");
        (await k.Svc.HelpAsync(As(Globex), null)).Select(a => a.Title).Should().BeEquivalentTo(
            "Connect to the VPN", "Globex holiday rota");

        // Asking for a staff-only article directly is no better than searching for it.
        var staffOnly = await k.H.Db.KbArticles.SingleAsync(a => a.Title == "Reset the Acme firewall");
        await Assert.ThrowsAsync<NotFoundException>(() => k.Svc.HelpArticleAsync(As(Acme), "Team", staffOnly.Id));
        var globexFaq = await k.H.Db.FaqArticles.SingleAsync(f => f.ClientCompanyId == Globex);
        await Assert.ThrowsAsync<NotFoundException>(() => k.Svc.HelpArticleAsync(As(Acme), "ClientFaq", globexFaq.Id));
    }

    [Fact]
    public async Task Searching_finds_articles_with_every_word_in_title_body_or_category()
    {
        var k = await BuildAsync();
        await SeedAsync(k);

        (await k.Svc.HelpAsync(As(Acme), "vpn forticlient")).Should().ContainSingle().Which.Title.Should().Be("Connect to the VPN");
        (await k.Svc.HelpAsync(As(Acme), "printers")).Select(a => a.Title).Should().BeEquivalentTo("Acme's scanner to email", "Where is the printer?");
        // The team's search sees the staff-only article too.
        (await k.Svc.ListAsync("firewall")).Should().ContainSingle().Which.Audience.Should().Be("Staff");
    }

    [Fact]
    public async Task Suggestions_rank_a_title_match_first_and_never_offer_what_the_client_cannot_read()
    {
        var k = await BuildAsync();
        await SeedAsync(k);

        var vpn = await k.Svc.SuggestAsync(As(Acme), "VPN not connecting from home");
        vpn.First().Title.Should().Be("Connect to the VPN");

        // "firewall" is only in a staff-only article: nothing to offer, not a leak.
        (await k.Svc.SuggestAsync(As(Acme), "firewall reset")).Should().BeEmpty();
        // Too little to go on.
        (await k.Svc.SuggestAsync(As(Acme), "it is not working")).Should().BeEmpty();
    }

    [Fact]
    public async Task This_solved_it_is_counted_per_article_and_only_for_an_article_the_client_could_read()
    {
        var k = await BuildAsync();
        await SeedAsync(k);
        var vpn = await k.H.Db.KbArticles.SingleAsync(a => a.Title == "Connect to the VPN");
        var faq = await k.H.Db.FaqArticles.SingleAsync(f => f.ClientCompanyId == Acme);
        var staffOnly = await k.H.Db.KbArticles.SingleAsync(a => a.Title == "Reset the Acme firewall");

        await k.Svc.SolvedAsync(As(Acme), "Team", vpn.Id, "VPN not connecting from home");
        await k.Svc.SolvedAsync(As(Globex), "Team", vpn.Id, "vpn drops");
        await k.Svc.SolvedAsync(As(Acme), "ClientFaq", faq.Id, "cant find printer");
        await Assert.ThrowsAsync<NotFoundException>(() => k.Svc.SolvedAsync(As(Acme), "Team", staffOnly.Id, "firewall"));

        var stats = await k.Svc.StatsAsync(30);
        stats.TicketsAvoided.Should().Be(3);
        stats.TopArticles.First().Should().BeEquivalentTo(new { Title = "Connect to the VPN", Solved = 2 });
        stats.Recent.Should().Contain(r => r.Query == "VPN not connecting from home" && r.ClientName == "Acme");
        (await k.Svc.ListAsync(null)).Single(a => a.Title == "Connect to the VPN").Solved.Should().Be(2);

        // Outside the window it is not counted.
        k.H.Clock.Advance(TimeSpan.FromDays(31));
        (await k.Svc.StatsAsync(30)).TicketsAvoided.Should().Be(0);
    }

    [Fact]
    public async Task An_article_for_chosen_clients_needs_at_least_one_and_only_this_organisations()
    {
        var k = await BuildAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() => WriteAsync(k, "For nobody", "x", "SelectedClients", clients: []));
        await Assert.ThrowsAsync<ValidationFailedException>(() => WriteAsync(k, "For a stranger", "x", "SelectedClients", clients: [Guid.NewGuid()]));

        var article = await WriteAsync(k, "Acme only", "x", "SelectedClients", clients: [Acme]);
        // Opened up to everyone: the list of chosen clients no longer applies, and is cleared.
        var opened = await k.Svc.SaveAsync(article.Id, new KbArticleInput("Acme only", "x", null, "AllClients", [Acme], true), Tech, "Rohan Mehta");
        opened.Should().BeEquivalentTo(new { Audience = "AllClients", ClientIds = Array.Empty<Guid>(), UpdatedByName = "Rohan Mehta", AuthorName = "Anika Sharma" });
    }
}
