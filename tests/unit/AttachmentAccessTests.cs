using Desk.Api.Controllers;
using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Who may fetch WHICH attachment on a ticket they are allowed to see.
///
/// The ticket detail withholds files posted with internal notes, but a list filter is not an access
/// control: an attachment id, once seen, does not expire, and this endpoint is what hands over the
/// bytes. These tests exercise the endpoint directly for that reason.
/// </summary>
public class AttachmentAccessTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();
    private static readonly Guid ClientUserId = Guid.NewGuid();

    private sealed class StubResolver(ClientAccess? access) : IClientAccessResolver
    {
        public Task<ClientAccess?> ResolveAsync(string idpSubject, CancellationToken ct = default)
            => Task.FromResult(access);
    }

    /// <summary>Issues a URL for anything asked of it — so a test that fails does so because the
    /// controller allowed the request, never because the service happened to refuse it.</summary>
    private sealed class AlwaysIssues : IAttachmentService
    {
        public Task<AttachmentDto> UploadAsync(UploadAttachmentInput input, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> GetDownloadUrlAsync(Guid attachmentId, CancellationToken ct = default)
            => Task.FromResult<string?>($"https://piomanage.com/api/attachments/blob?key=k&exp=1&sig=s#{attachmentId}");
    }

    private sealed class Fixture
    {
        public required AdminHarness H { get; init; }
        public required Guid TicketId { get; init; }
        public required Guid PublicFileId { get; init; }
        public required Guid InternalFileId { get; init; }
        public required Guid LooseFileId { get; init; }
    }

    private static async Task<Fixture> SetupAsync()
    {
        var h = AdminHarness.Create(Org);
        var ticket = new Ticket
        {
            MspOrganizationId = Org, PsaConnectionId = Guid.NewGuid(), Provider = ProviderType.AutotaskPsa,
            ClientCompanyId = Company, RequesterUserId = ClientUserId,
            RequesterName = "R", RequesterEmail = "r@x", Title = "t",
            PortalStatus = "NEW", PortalPriority = "NORMAL",
        };
        h.Db.Tickets.Add(ticket);

        var publicNote = new TicketNote { MspOrganizationId = Org, TicketId = ticket.Id, AuthorName = "Tech", Body = "public", IsPublic = true };
        var internalNote = new TicketNote { MspOrganizationId = Org, TicketId = ticket.Id, AuthorName = "Tech", Body = "internal", IsPublic = false };
        h.Db.TicketNotes.AddRange(publicNote, internalNote);

        var onPublic = File(ticket.Id, publicNote.Id, "reply.png");
        var onInternal = File(ticket.Id, internalNote.Id, "internal-workaround.png");
        var loose = File(ticket.Id, null, "raised-with-the-ticket.png");
        h.Db.TicketAttachments.AddRange(onPublic, onInternal, loose);
        await h.Db.SaveChangesAsync();

        return new Fixture
        {
            H = h, TicketId = ticket.Id,
            PublicFileId = onPublic.Id, InternalFileId = onInternal.Id, LooseFileId = loose.Id,
        };
    }

    private static TicketAttachment File(Guid ticketId, Guid? noteId, string name) => new()
    {
        MspOrganizationId = Org, TicketId = ticketId, TicketNoteId = noteId,
        OriginalFileName = name, ContentType = "image/png", SizeBytes = 512,
        StorageObjectKey = $"att/{Guid.NewGuid()}/{Guid.NewGuid():N}.png",
        ScanStatus = AttachmentScanStatus.Clean,
    };

    private static AttachmentsController AsClient(Fixture f) => new(
        new TestCurrentUser(Org, subject: "sub-client", permissions: new HashSet<string>()),
        new StubResolver(new ClientAccess(Org, Company, ClientUserId, IsCompanyAdministrator: true)),
        new AlwaysIssues(), f.H.Db);

    private static AttachmentsController AsStaff(Fixture f) => new(
        new TestCurrentUser(Org, subject: "sub-staff",
            permissions: new HashSet<string> { Permissions.TicketsViewAll }, userId: Guid.NewGuid()),
        new StubResolver(null),
        new AlwaysIssues(), f.H.Db);

    [Fact]
    public async Task A_client_cannot_download_a_file_posted_with_an_internal_note()
    {
        // Belonging to a ticket the caller may see was the ONLY check here. The note was withheld
        // and its attachment was not, so an id lifted from the detail payload fetched the file.
        var f = await SetupAsync();

        var act = async () => await AsClient(f).DownloadUrl(f.TicketId, f.InternalFileId, default);

        // Not found, not forbidden: "you may not have this one" confirms there is one to have.
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_client_can_still_download_files_from_the_thread_they_can_read()
    {
        // The other half. A rule that refuses the internal file and the public one alike would
        // read as "attachments are broken again", which is where this whole thread started.
        var f = await SetupAsync();
        var controller = AsClient(f);

        (await controller.DownloadUrl(f.TicketId, f.PublicFileId, default)).Should().BeOfType<OkObjectResult>();
        (await controller.DownloadUrl(f.TicketId, f.LooseFileId, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Staff_can_download_the_file_on_an_internal_note()
    {
        // Internal material is the technician view's purpose; the restriction is on the client
        // path only, and a check written against the wrong subject would take it from both.
        var f = await SetupAsync();

        (await AsStaff(f).DownloadUrl(f.TicketId, f.InternalFileId, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task An_attachment_from_another_ticket_is_refused_whoever_asks()
    {
        // The pre-existing binding between ticket and attachment, kept under test while the code
        // around it changed shape.
        var f = await SetupAsync();
        var other = await SetupAsync();

        var asClient = async () => await AsClient(f).DownloadUrl(f.TicketId, other.LooseFileId, default);
        await asClient.Should().ThrowAsync<NotFoundException>();

        var asStaff = async () => await AsStaff(f).DownloadUrl(f.TicketId, other.LooseFileId, default);
        await asStaff.Should().ThrowAsync<NotFoundException>();
    }
}
