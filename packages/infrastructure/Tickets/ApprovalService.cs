using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Authorization;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Tickets;

/// <summary>
/// Approvals: a technician asks one of the client's named approvers to agree to something before the
/// work goes ahead, and the approver answers in the portal - or the technician writes down an answer
/// given by phone.
///
/// While the question is open the ticket waits on the customer, which is what stops the SLA clock:
/// on a PSA ticket the provider's own "waiting" status does it, on a board ticket the portal's plan
/// does. Either answer moves it back to in progress, because either way the next move is the team's.
///
/// Every step leaves a public note, so the PSA's own record says who approved what and when, in the
/// same thread the client reads.
/// </summary>
public sealed class ApprovalService(
    DeskDbContext db,
    ITicketScopeQuery scopeQuery,
    TicketStatusWriter statusWriter,
    ITicketCommandService commands,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<ApprovalService> logger) : IApprovalService
{
    /// <summary>The status a ticket waits in while an approval is open.</summary>
    public const string WaitingStatus = "WAITING_CUSTOMER";

    /// <summary>Where it goes once the approver has answered.</summary>
    public const string ResumeStatus = "IN_PROGRESS";

    // ---- Staff ----

    public async Task<StaffApprovalsDto> StaffViewAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await StaffTicketAsync(appUserId, ticketId, Permissions.TicketsViewAll, ct);
        var approvals = await ListAsync(ticket.Id, null, ct);
        var reason = await WhyNotAskAsync(ticket, ct);
        var approvers = ticket.ClientCompanyId is { } company ? await ApproversAsync(company, ct) : [];
        if (reason is null && approvers.Count == 0)
            reason = "This client has no approvers listed yet. Their administrator adds them in the Control Panel, under Approvers.";
        var applies = ticket.ClientCompanyId is not null && await ClientCanSeeAsync(ticket, ct);
        return new StaffApprovalsDto(applies, reason is null, reason, approvals, approvers);
    }

    public async Task<TicketApprovalDto> RequestAsync(
        Guid appUserId, string authorName, Guid ticketId, ApprovalRequestInput input, CancellationToken ct = default)
    {
        var request = Clean(input.Request, TicketApproval.MaxRequestLength, "what needs approving")
            ?? throw new ValidationFailedException("Say what needs approving.");
        var ticket = await StaffTicketAsync(appUserId, ticketId, Permissions.TicketsUpdate, ct);
        if (await WhyNotAskAsync(ticket, ct) is { } reason) throw new ValidationFailedException(reason);

        // Only someone on THIS ticket's client's list. An id from another company is simply not found.
        var approver = await db.Approvers.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == input.ApproverId && a.ClientCompanyId == ticket.ClientCompanyId, ct)
            ?? throw new NotFoundException("Approver");

        // The status first: it is the step that can be refused (by the PSA), and a request the ticket
        // does not reflect would leave the SLA running on a question the client has not answered.
        if (!SlaClock.IsWaiting(ticket.PortalStatus))
            await statusWriter.SetAsync(ticket, WaitingStatus, ct);

        var row = new TicketApproval
        {
            MspOrganizationId = ticket.MspOrganizationId,
            TicketId = ticket.Id,
            ClientCompanyId = ticket.ClientCompanyId!.Value,
            ApproverId = approver.Id,
            ApproverName = approver.Name,
            ApproverEmail = string.IsNullOrWhiteSpace(approver.Email) ? null : approver.Email.Trim(),
            Request = request,
            RequestedByAppUserId = appUserId,
            RequestedByName = authorName,
            RequestedAt = clock.GetUtcNow(),
        };
        db.TicketApprovals.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.approval.requested", nameof(TicketApproval), row.Id.ToString(),
            new { ticket.Id, approver = approver.Name }, ct);

        await TrailAsync(ticket.Id, authorName, false, $"Approval requested from {approver.Name}: {request}", ct);
        return Dto(row, canAnswer: false);
    }

    public async Task<TicketApprovalDto> RecordAsync(
        Guid appUserId, string authorName, Guid approvalId, ApprovalRecordInput input, CancellationToken ct = default)
    {
        // Portal is the approver's own click, and only the approver can make it.
        if (!Enum.TryParse<ApprovalChannel>(input.Channel, true, out var channel) || channel == ApprovalChannel.Portal)
            throw new ValidationFailedException("Say how the answer reached you: by phone, by email or in person.");
        var comment = Clean(input.Comment, TicketApproval.MaxCommentLength, "the note");

        var row = await db.TicketApprovals.FirstOrDefaultAsync(a => a.Id == approvalId, ct) ?? throw new NotFoundException("Approval");
        var ticket = await StaffTicketAsync(appUserId, row.TicketId, Permissions.TicketsUpdate, ct);
        EnsurePending(row);

        row.State = input.Approved ? ApprovalState.Approved : ApprovalState.Rejected;
        row.DecidedAt = clock.GetUtcNow();
        row.DecisionComment = comment;
        row.Channel = channel;
        row.RecordedByAppUserId = appUserId;
        row.RecordedByName = authorName;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.approval.recorded", nameof(TicketApproval), row.Id.ToString(),
            new { state = row.State.ToString(), channel = channel.ToString() }, ct);

        var how = channel switch { ApprovalChannel.Phone => "by phone", ApprovalChannel.Email => "by email", _ => "in person" };
        await TrailAsync(ticket.Id, authorName, false,
            $"{Verb(row.State)} by {row.ApproverName} {how}, recorded by {authorName}.{Suffix(comment)}", ct);
        await ResumeAsync(ticket, ct);
        return Dto(row, canAnswer: false);
    }

    public async Task<TicketApprovalDto> CancelAsync(Guid appUserId, string authorName, Guid approvalId, CancellationToken ct = default)
    {
        var row = await db.TicketApprovals.FirstOrDefaultAsync(a => a.Id == approvalId, ct) ?? throw new NotFoundException("Approval");
        var ticket = await StaffTicketAsync(appUserId, row.TicketId, Permissions.TicketsUpdate, ct);
        EnsurePending(row);

        row.State = ApprovalState.Cancelled;
        row.DecidedAt = clock.GetUtcNow();
        row.RecordedByAppUserId = appUserId;
        row.RecordedByName = authorName;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.approval.cancelled", nameof(TicketApproval), row.Id.ToString(), null, ct);

        await TrailAsync(ticket.Id, authorName, false, $"Approval request to {row.ApproverName} withdrawn by {authorName}.", ct);
        await ResumeAsync(ticket, ct);
        return Dto(row, canAnswer: false);
    }

    // ---- Client ----

    public async Task<IReadOnlyList<TicketApprovalDto>> ClientViewAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default)
    {
        // Exactly the tickets the client can open - never an internal board's, never another company's.
        var visible = await TicketReadService.ClientVisible(db, access).AnyAsync(t => t.Id == ticketId, ct);
        if (!visible) throw new NotFoundException("Ticket");
        return await ListAsync(ticketId, await EmailOfAsync(access, ct), ct);
    }

    public async Task<IReadOnlyList<MyApprovalDto>> MineAsync(ClientAccess access, CancellationToken ct = default)
    {
        var email = await EmailOfAsync(access, ct);
        if (email is null) return [];

        // Addressed by email, not by who can see the ticket: the finance head who approves purchases
        // did not raise the ticket and need not be a company administrator to answer it.
        return await db.TicketApprovals.AsNoTracking()
            .Where(a => a.ClientCompanyId == access.ClientCompanyId
                        && a.State == ApprovalState.Pending
                        && a.ApproverEmail != null
                        && a.ApproverEmail.ToLower() == email)
            .OrderBy(a => a.RequestedAt)
            .Select(a => new MyApprovalDto(
                a.Id, a.TicketId,
                db.Tickets.Where(t => t.Id == a.TicketId).Select(t => t.Number ?? t.ExternalTicketId).FirstOrDefault() ?? "—",
                db.Tickets.Where(t => t.Id == a.TicketId).Select(t => t.Title).FirstOrDefault() ?? "",
                a.Request, a.RequestedByName, a.RequestedAt))
            .ToListAsync(ct);
    }

    public async Task<TicketApprovalDto> DecideAsync(
        ClientAccess access, Guid approvalId, bool approved, string? comment, CancellationToken ct = default)
    {
        var clean = Clean(comment, TicketApproval.MaxCommentLength, "the comment");
        var row = await db.TicketApprovals
            .FirstOrDefaultAsync(a => a.Id == approvalId && a.ClientCompanyId == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Approval");
        var user = await db.ClientUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == access.ClientUserId, ct)
            ?? throw new NotFoundException("Client user");
        if (!SameEmail(row.ApproverEmail, user.Email))
            throw new ForbiddenException($"This request is waiting for {row.ApproverName}, and only they can answer it.");
        EnsurePending(row);

        row.State = approved ? ApprovalState.Approved : ApprovalState.Rejected;
        row.DecidedAt = clock.GetUtcNow();
        row.DecisionComment = clean;
        row.Channel = ApprovalChannel.Portal;
        row.DecidedByClientUserId = user.Id;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("ticket.approval.decided", nameof(TicketApproval), row.Id.ToString(),
            new { state = row.State.ToString() }, ct);

        await TrailAsync(row.TicketId, user.DisplayName, true, $"{Verb(row.State)}: {row.Request}{Suffix(clean)}", ct);
        var ticket = await db.Tickets.FirstAsync(t => t.Id == row.TicketId, ct);
        await ResumeAsync(ticket, ct);
        return Dto(row, canAnswer: false);
    }

    // ---- Shared ----

    /// <summary>Why this ticket cannot be sent for approval, or null when it can.</summary>
    private async Task<string?> WhyNotAskAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.ClientCompanyId is null)
            return "This ticket has no client, so there is nobody to ask.";
        if (!await ClientCanSeeAsync(ticket, ct))
            return "The client cannot see tickets on this board, so they cannot be asked to approve one.";
        if (TicketStatusRules.Finished(ticket.PortalStatus))
            return "This ticket is finished. Reopen it before asking for approval.";
        if (await db.TicketApprovals.AnyAsync(a => a.TicketId == ticket.Id && a.State == ApprovalState.Pending, ct))
            return "An approval is already waiting on this ticket. Record its answer or withdraw it first.";
        return null;
    }

    private async Task<bool> ClientCanSeeAsync(Ticket ticket, CancellationToken ct) =>
        ticket.Origin == TicketOrigin.Psa || await db.Boards.AnyAsync(b => b.Id == ticket.BoardId && b.ClientVisible, ct);

    private async Task<IReadOnlyList<ApproverChoiceDto>> ApproversAsync(Guid clientCompanyId, CancellationToken ct)
    {
        var approvers = await db.Approvers.AsNoTracking()
            .Where(a => a.ClientCompanyId == clientCompanyId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.Email, a.Scope })
            .ToListAsync(ct);
        var logins = (await db.ClientUsers.AsNoTracking()
                .Where(u => u.ClientCompanyId == clientCompanyId && u.IsActive)
                .Select(u => u.Email).ToListAsync(ct))
            .Select(e => e.Trim().ToLowerInvariant()).ToHashSet();
        return [.. approvers.Select(a => new ApproverChoiceDto(a.Id, a.Name, a.Email, a.Scope,
            a.Email is not null && logins.Contains(a.Email.Trim().ToLowerInvariant())))];
    }

    private async Task<IReadOnlyList<TicketApprovalDto>> ListAsync(Guid ticketId, string? viewerEmail, CancellationToken ct)
    {
        var rows = await db.TicketApprovals.AsNoTracking()
            .Where(a => a.TicketId == ticketId)
            .OrderByDescending(a => a.RequestedAt)
            .ToListAsync(ct);
        return [.. rows.Select(r => Dto(r, r.State == ApprovalState.Pending && SameEmail(r.ApproverEmail, viewerEmail)))];
    }

    private async Task<Ticket> StaffTicketAsync(Guid appUserId, Guid ticketId, string permission, CancellationToken ct)
        => await scopeQuery.FindAsync(db.Tickets, ticketId, appUserId, permission, ct) ?? throw new NotFoundException("Ticket");

    private async Task<string?> EmailOfAsync(ClientAccess access, CancellationToken ct)
    {
        var email = await db.ClientUsers.AsNoTracking()
            .Where(u => u.Id == access.ClientUserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Back to the team once the question is settled - but only from the waiting status the question
    /// put it in. A technician who has since put it on hold, or closed it, made a later decision, and
    /// an approval arriving afterwards must not undo it.
    /// </summary>
    private async Task ResumeAsync(Ticket ticket, CancellationToken ct)
    {
        if (!string.Equals(ticket.PortalStatus?.Trim(), WaitingStatus, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await statusWriter.SetAsync(ticket, ResumeStatus, ct);
        }
        catch (Exception ex) when (ex is ValidationFailedException or HttpRequestException)
        {
            // The answer is saved and is what matters to the person who gave it; a PSA refusing the
            // status change must not make it look as if their answer failed. Left waiting, the ticket
            // shows the technician it still needs moving on.
            logger.LogWarning(ex, "Approval answered on ticket {TicketId} but the status could not be moved on", ticket.Id);
        }
    }

    /// <summary>The note that records a step. A PSA refusing the note does not undo the step itself.</summary>
    private async Task TrailAsync(Guid ticketId, string author, bool byClient, string body, CancellationToken ct)
    {
        try
        {
            await commands.PostTrailNoteAsync(ticketId, author, byClient, body, ct);
        }
        catch (Exception ex) when (ex is ValidationFailedException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not add the approval note to ticket {TicketId}", ticketId);
        }
    }

    private static void EnsurePending(TicketApproval row)
    {
        if (row.State != ApprovalState.Pending)
            throw new ValidationFailedException(row.State == ApprovalState.Cancelled
                ? "This request was withdrawn."
                : "This request has already been answered.");
    }

    private static bool SameEmail(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value, int max, string what)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (clean is { Length: var n } && n > max)
            throw new ValidationFailedException($"Keep {what} to {max:N0} characters.");
        return clean;
    }

    private static string Verb(ApprovalState state) => state == ApprovalState.Approved ? "Approved" : "Rejected";

    private static string Suffix(string? comment) => comment is null ? "" : $" “{comment}”";

    private static TicketApprovalDto Dto(TicketApproval r, bool canAnswer) => new(
        r.Id, r.ApproverName, r.Request, r.RequestedByName, r.RequestedAt, r.State.ToString(),
        r.DecidedAt, r.DecisionComment, r.Channel?.ToString(), r.RecordedByName, canAnswer);
}
