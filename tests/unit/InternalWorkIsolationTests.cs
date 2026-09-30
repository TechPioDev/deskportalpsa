using System.Reflection;
using Desk.Api.Auth;
using Desk.Api.Controllers;
using Desk.Application.Attachments;
using Desk.Application.Authorization;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Boards;
using Desk.Infrastructure.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The team's own work, and the ways it could reach a client or a colleague who should not see it.
/// Each test here was a way in: a client searching internal notes, fetching or attaching files on an
/// internal ticket, confirming one exists by commenting on it, listing the team's boards; a technician
/// posting onto a restricted board; an auditor reading ticket titles through the attention list; and
/// free text in a monitoring alert choosing which company's administrators see it.
/// </summary>
public class InternalWorkIsolationTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid ClientAdmin = Guid.NewGuid();

    private sealed record World(AdminHarness H, ClientCompany Company, Ticket Psa, Ticket Internal, ClientAccess Client);

    /// <summary>
    /// One client company with two tickets: its own PSA ticket, and internal work the team filed under
    /// it. The caller is that company's administrator - the widest client view there is.
    /// </summary>
    private static async Task<World> WorldAsync()
    {
        var h = AdminHarness.Create(Org);
        var company = new ClientCompany { MspOrganizationId = Org, Name = "Acme Dental", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "1001" };
        var board = new Board { MspOrganizationId = Org, Name = "Internal IT", Key = "INT", Kind = BoardKind.Internal };
        Ticket T(string title) => new()
        {
            MspOrganizationId = Org, ClientCompanyId = company.Id, Title = title, RequesterName = "Priya",
            RequesterEmail = "priya@acme.test", PortalStatus = "NEW", PortalPriority = "NORMAL",
        };
        var psa = T("Printer offline");
        psa.PsaConnectionId = company.PsaConnectionId;
        psa.Provider = ProviderType.AutotaskPsa;
        psa.ExternalTicketId = "T20260930.0001";
        var inside = T("Acme migration rehearsal");
        inside.Origin = TicketOrigin.Internal;
        inside.BoardId = board.Id;
        inside.Number = "INT-000001";
        h.Db.AddRange(company, board, psa, inside);
        h.Db.TicketNotes.AddRange(
            new TicketNote { MspOrganizationId = Org, TicketId = psa.Id, AuthorName = "Tech", Body = "Replaced the toner", IsPublic = true },
            new TicketNote { MspOrganizationId = Org, TicketId = psa.Id, AuthorName = "Tech", Body = "Client keeps unplugging it BLUNTPHRASE", IsPublic = false });
        await h.Db.SaveChangesAsync();
        return new World(h, company, psa, inside, new ClientAccess(Org, company.Id, ClientAdmin, IsCompanyAdministrator: true));
    }

    private sealed class Resolver(ClientAccess? access) : IClientAccessResolver
    {
        public Task<ClientAccess?> ResolveAsync(string idpSubject, CancellationToken ct = default) => Task.FromResult(access);
    }

    private sealed class NeverUploads : IAttachmentService
    {
        public Task<AttachmentDto> UploadAsync(UploadAttachmentInput input, CancellationToken ct = default)
            => throw new InvalidOperationException("The upload was allowed through.");

        public Task<string?> GetDownloadUrlAsync(Guid attachmentId, CancellationToken ct = default)
            => Task.FromResult<string?>("https://piomanage.com/api/attachments/blob?key=k");
    }

    private static AttachmentsController AsClient(World w) => new(
        new TestCurrentUser(Org, subject: "sub-client", permissions: new HashSet<string>()),
        new Resolver(w.Client), new NeverUploads(), w.H.Db, new NoopTicketScopeQuery());

    private static AttachmentsController AsStaff(World w) => new(
        new TestCurrentUser(Org, subject: "sub-staff", permissions: new HashSet<string> { Permissions.TicketsViewAll }, userId: Guid.NewGuid()),
        new Resolver(null), new NeverUploads(), w.H.Db, new NoopTicketScopeQuery());

    private static async Task<Guid> PsaFileAsync(World w)
    {
        var f = new TicketAttachment
        {
            MspOrganizationId = Org, TicketId = w.Psa.Id, OriginalFileName = "invoice.pdf", ContentType = "application/pdf",
            SizeBytes = 10, StorageObjectKey = "att/p/q.pdf", ScanStatus = AttachmentScanStatus.Clean,
        };
        w.H.Db.TicketAttachments.Add(f);
        await w.H.Db.SaveChangesAsync();
        return f.Id;
    }

    private static IFormFile Png() => new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "photo.png");

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_searching_the_conversation_does_not_match_internal_notes()
    {
        var w = await WorldAsync();
        var reads = new TicketReadService(w.H.Db, new NoopTicketScopeQuery(), w.H.User);

        // A match on an internal note told the client, one phrase at a time, what the team wrote.
        (await reads.SearchAsync(new TicketQuery(Q: "bluntphrase", IncludeNotes: true), w.Client)).Total.Should().Be(0);
        (await reads.SearchAsync(new TicketQuery(Q: "toner", IncludeNotes: true), w.Client)).Total.Should().Be(1);
        // Staff still search everything they can read.
        var staff = new TicketReadService(w.H.Db, new NoopTicketScopeQuery(), new TestCurrentUser(Org, userId: Guid.NewGuid()));
        (await staff.SearchAsync(new TicketQuery(Q: "bluntphrase", IncludeNotes: true))).Total.Should().Be(1);
    }

    // ── Attachments ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_cannot_fetch_or_add_files_on_internal_work_filed_under_their_company()
    {
        var w = await WorldAsync();
        var file = new TicketAttachment
        {
            MspOrganizationId = Org, TicketId = w.Internal.Id, OriginalFileName = "rehearsal-plan.pdf",
            ContentType = "application/pdf", SizeBytes = 10, StorageObjectKey = "att/x/y.pdf", ScanStatus = AttachmentScanStatus.Clean,
        };
        w.H.Db.TicketAttachments.Add(file);
        await w.H.Db.SaveChangesAsync();

        // Company and requester were the only checks, so an administrator of the company reached the
        // files of any ticket carrying its name - internal ones included.
        // Not found, not forbidden: to a client, internal work does not exist.
        await AsClient(w).Invoking(c => c.DownloadUrl(w.Internal.Id, file.Id, default)).Should().ThrowAsync<NotFoundException>();
        await AsClient(w).Invoking(c => c.Upload(w.Internal.Id, Png(), null, default)).Should().ThrowAsync<NotFoundException>();
        // Their own ticket still works.
        (await AsClient(w).DownloadUrl(w.Psa.Id, await PsaFileAsync(w), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_file_can_only_be_attached_to_a_note_on_the_same_ticket()
    {
        var w = await WorldAsync();
        var elsewhere = new TicketNote { MspOrganizationId = Org, TicketId = w.Internal.Id, AuthorName = "Tech", Body = "x", IsPublic = false };
        w.H.Db.TicketNotes.Add(elsewhere);
        await w.H.Db.SaveChangesAsync();

        // The note travels to the provider with the file; another ticket's note put the file there.
        await AsStaff(w).Invoking(c => c.Upload(w.Psa.Id, Png(), elsewhere.Id, default)).Should().ThrowAsync<NotFoundException>();

        // And a client may attach only to what a client can read.
        var internalNote = await w.H.Db.TicketNotes.FirstAsync(n => n.TicketId == w.Psa.Id && !n.IsPublic);
        await AsClient(w).Invoking(c => c.Upload(w.Psa.Id, Png(), internalNote.Id, default)).Should().ThrowAsync<NotFoundException>();
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_commenting_on_internal_work_is_told_it_does_not_exist()
    {
        var w = await WorldAsync();
        // The caller exists, so the old path got past "who is this" to the PSA check that gave it away.
        w.H.Db.ClientUsers.Add(new ClientUser
        {
            Id = ClientAdmin, MspOrganizationId = Org, ClientCompanyId = w.Company.Id,
            Email = "admin@acme.test", DisplayName = "Acme Admin", IsCompanyAdministrator = true,
        });
        await w.H.Db.SaveChangesAsync();
        var svc = new TicketCommandService(w.H.Db, null!, null!, null!, new NoopTicketScopeQuery(), w.H.Clock, new RecordingActivity());

        // It used to get as far as "this ticket does not belong to a PSA" - which confirmed it exists.
        await svc.Invoking(s => s.AddCommentAsync(w.Client, w.Internal.Id, "Hello?")).Should().ThrowAsync<NotFoundException>();
    }

    // ── The team's boards ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(BoardsController))]
    [InlineData(typeof(SlaPlansController))]
    [InlineData(typeof(RecurringTicketsController))]
    public void The_board_routes_are_for_staff_even_though_clients_may_raise_tickets(Type controller)
    {
        // Clients hold tickets.create to raise their own tickets; that key alone opened these routes -
        // every internal board's name, open count and default assignees. A class-level staff key is
        // ANDed with each action's own.
        var staffOnly = controller.GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
            .Select(a => a.Policy).ToList();
        staffOnly.Should().Contain(PermissionPolicyProvider.For(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned));
    }

    [Fact]
    public async Task Only_members_may_raise_a_ticket_on_a_board_limited_to_them()
    {
        var h = AdminHarness.Create(Org);
        AppUser Person(string name) => new() { MspOrganizationId = Org, DisplayName = name, Email = $"{name}@techpio.test", IsActive = true };
        var member = Person("anika");
        var outsider = Person("arjun");
        var restricted = new Board { MspOrganizationId = Org, Name = "Leadership", Key = "LDR", Kind = BoardKind.Internal };
        var open = new Board { MspOrganizationId = Org, Name = "Office IT", Key = "OIT", Kind = BoardKind.Internal };
        h.Db.AddRange(member, outsider, restricted, open);
        h.Db.BoardMembers.Add(new BoardMember { MspOrganizationId = Org, BoardId = restricted.Id, AppUserId = member.Id });
        await h.Db.SaveChangesAsync();
        var tickets = new InternalTicketService(h.Db, h.Tenant, h.Clock, new RecordingActivity());

        // A board limited to its members was open to anyone who may raise tickets - including people
        // who then could not read what they had posted.
        await tickets.Invoking(t => t.CreateAsync(outsider.Id, new InternalTicketInput(restricted.Id, "Salary review", null)))
            .Should().ThrowAsync<ForbiddenException>();
        (await tickets.CreateAsync(member.Id, new InternalTicketInput(restricted.Id, "Salary review", null))).Number.Should().Be("LDR-000001");
        (await tickets.CreateAsync(outsider.Id, new InternalTicketInput(open.Id, "Wi-Fi drops at 3pm", null))).Number.Should().Be("OIT-000001");
    }

    // ── The attention list ────────────────────────────────────────────────────

    private sealed class Scoped(PermissionScope scope) : IEffectivePermissionService
    {
        public Task<EffectivePermission> ResolveAsync(Guid appUserId, string permissionKey, CancellationToken ct = default)
            => Task.FromResult(new EffectivePermission(permissionKey, scope, default, BoardAccessMode.All, []));
    }

    [Theory]
    [InlineData(PermissionScope.None)]        // the Auditor: health access, no ticket access at all
    [InlineData(PermissionScope.Department)]  // a lead who sees part of the organization
    public async Task Organization_wide_ticket_lists_need_sight_of_every_ticket(PermissionScope scope)
    {
        var controller = new AdminReadController(null!, null!, null!, null!, null!, null!);
        var user = new TestCurrentUser(Org, userId: Guid.NewGuid());

        // Both lists name tickets - titles, references, a client's comment - from across the organization.
        await controller.Invoking(c => c.Attention(null!, user, new Scoped(scope), default)).Should().ThrowAsync<ForbiddenException>();
        await controller.Invoking(c => c.Unsynced(null, user, new Scoped(scope), default)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Someone_who_sees_every_ticket_still_gets_the_attention_list()
    {
        var user = new TestCurrentUser(Org, userId: Guid.NewGuid());
        (await user.SeesEveryTicketAsync(new Scoped(PermissionScope.All), default)).Should().BeTrue();
        (await new TestCurrentUser(Org).SeesEveryTicketAsync(new Scoped(PermissionScope.All), default))
            .Should().BeFalse("no portal identity, nothing to resolve");
    }

    // ── Monitoring alerts ─────────────────────────────────────────────────────

    private static (AlertSourceService Sources, AlertIntakeService Intake, AdminHarness H) Alerts()
    {
        var h = AdminHarness.Create(Org);
        var audit = new AuditWriter(h.Db, h.User, h.Tenant, h.Clock);
        return (new AlertSourceService(h.Db, h.Tenant, h.User, audit),
                new AlertIntakeService(h.Db, h.Tenant, h.Clock, new RecordingActivity(), NullLogger<AlertIntakeService>.Instance),
                h);
    }

    private static async Task<(ClientCompany Acme, ClientCompany Other, Board Published)> ClientsAndPublishedBoardAsync(AdminHarness h)
    {
        var acme = new ClientCompany { MspOrganizationId = Org, Name = "Acme Dental", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "1" };
        var other = new ClientCompany { MspOrganizationId = Org, Name = "Other Co", PsaConnectionId = Guid.NewGuid(), ExternalCompanyId = "2" };
        var board = new Board { MspOrganizationId = Org, Name = "Monitoring", Key = "MON", Kind = BoardKind.Rmm, ClientVisible = true };
        h.Db.AddRange(acme, other, board);
        await h.Db.SaveChangesAsync();
        return (acme, other, board);
    }

    [Fact]
    public async Task A_pinned_source_files_every_alert_under_its_client_whatever_the_alert_says()
    {
        var (sources, intake, h) = Alerts();
        var (acme, _, board) = await ClientsAndPublishedBoardAsync(h);
        var source = await sources.CreateAsync(new AlertSourceInput("Acme NinjaOne", board.Id, ClientCompanyId: acme.Id));

        await intake.ReceiveAsync(source.Key, new AlertMessage("a-1", "Disk C: 95% full", Client: "Other Co"));

        (await h.Db.Tickets.SingleAsync()).ClientCompanyId.Should().Be(acme.Id);
        source.Source.ClientName.Should().Be("Acme Dental");
    }

    [Fact]
    public async Task On_a_board_shown_to_clients_an_unpinned_source_never_picks_the_client_from_the_text()
    {
        var (sources, intake, h) = Alerts();
        var (_, other, board) = await ClientsAndPublishedBoardAsync(h);
        var source = await sources.CreateAsync(new AlertSourceInput("Shared NinjaOne", board.Id));

        var result = await intake.ReceiveAsync(source.Key, new AlertMessage("a-1", "Disk C: 95% full", Client: "Other Co"));

        // The tool is told why - not "no such client", which is not true.
        result.Detail.Should().Contain("pinned").And.NotContain("No client here");
        // Free text decided whose administrators read the alert. Now it stays with the team, and the
        // source says how to publish it.
        (await h.Db.Tickets.SingleAsync()).ClientCompanyId.Should().BeNull();
        other.Id.Should().NotBeEmpty();
        (await h.Db.AlertSources.SingleAsync()).LastError.Should().Contain("pinned");
    }

    [Fact]
    public async Task Pinning_a_source_to_a_client_that_is_not_here_is_refused()
    {
        var (sources, _, h) = Alerts();
        var (_, _, board) = await ClientsAndPublishedBoardAsync(h);

        await sources.Invoking(s => s.CreateAsync(new AlertSourceInput("x", board.Id, ClientCompanyId: Guid.NewGuid())))
            .Should().ThrowAsync<ValidationFailedException>();
    }
}
