namespace Desk.Application.Tickets;

/// <summary>An approval as the ticket page shows it, to staff and to the client alike.</summary>
public sealed record TicketApprovalDto(
    Guid Id,
    string ApproverName,
    string Request,
    string RequestedByName,
    DateTimeOffset RequestedAt,
    string State,
    DateTimeOffset? DecidedAt,
    string? DecisionComment,
    /// <summary>"Portal", or how the technician says the answer reached them: Phone, Email, InPerson.</summary>
    string? Channel,
    string? RecordedByName,
    /// <summary>For the client viewing it: whether they are the person being asked and can answer now.</summary>
    bool CanAnswer);

/// <summary>Someone on the client's approver list, as a technician picks from it.</summary>
public sealed record ApproverChoiceDto(
    Guid Id, string Name, string? Email, string? Scope,
    /// <summary>Whether a portal login with this email exists at the client, i.e. whether they can
    /// answer in the portal. Without one the technician records the answer instead.</summary>
    bool CanAnswerInPortal);

/// <summary>What the technician sees on a ticket: its approvals, and who they could ask.</summary>
public sealed record StaffApprovalsDto(
    /// <summary>Whether approvals apply to this ticket at all: it has a client, and the client can see
    /// it. False on the team's own internal work, where the panel has nothing to offer.</summary>
    bool Applies,
    bool CanAsk,
    /// <summary>Why nobody can be asked on this ticket, when that is the case.</summary>
    string? Reason,
    IReadOnlyList<TicketApprovalDto> Approvals,
    IReadOnlyList<ApproverChoiceDto> Approvers);

/// <summary>A request waiting on the signed-in client user, for their own list.</summary>
public sealed record MyApprovalDto(
    Guid Id, Guid TicketId, string Reference, string TicketTitle, string Request,
    string RequestedByName, DateTimeOffset RequestedAt);

public sealed record ApprovalRequestInput(Guid ApproverId, string Request);

public sealed record ApprovalRecordInput(bool Approved, string Channel, string? Comment);

public interface IApprovalService
{
    // ---- Staff ----
    Task<StaffApprovalsDto> StaffViewAsync(Guid appUserId, Guid ticketId, CancellationToken ct = default);
    Task<TicketApprovalDto> RequestAsync(Guid appUserId, string authorName, Guid ticketId, ApprovalRequestInput input, CancellationToken ct = default);
    Task<TicketApprovalDto> RecordAsync(Guid appUserId, string authorName, Guid approvalId, ApprovalRecordInput input, CancellationToken ct = default);
    Task<TicketApprovalDto> CancelAsync(Guid appUserId, string authorName, Guid approvalId, CancellationToken ct = default);

    // ---- Client ----
    Task<IReadOnlyList<TicketApprovalDto>> ClientViewAsync(ClientAccess access, Guid ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<MyApprovalDto>> MineAsync(ClientAccess access, CancellationToken ct = default);
    Task<TicketApprovalDto> DecideAsync(ClientAccess access, Guid approvalId, bool approved, string? comment, CancellationToken ct = default);
}
