using Desk.Domain.Common;
using Desk.Domain.Enums;

namespace Desk.Domain.Tickets;

/// <summary>
/// A board the team works on that is NOT a PSA queue: the team's own work, or a monitoring tool's
/// alerts. Tickets from a PSA keep their provider queue and never appear here, so the two kinds of
/// work are never mixed in one list.
///
/// Boards are created by leads and administrators; who may raise or take a ticket on one is a
/// separate question, and the answer is anybody on the team (see <see cref="BoardMember"/>).
/// </summary>
public class Board : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>
    /// Short prefix for the ticket numbers on this board, e.g. "INT" giving INT-000123. Stable once
    /// tickets exist, because people quote these numbers to each other.
    /// </summary>
    public required string Key { get; set; }

    public string? Description { get; set; }

    public BoardKind Kind { get; set; } = BoardKind.Internal;

    /// <summary>
    /// Whether the client this ticket names may see it in their portal. Always false for an internal
    /// board: work recorded against a client is still the team's own record, not a client-facing
    /// thread. An RMM board may turn it on, so a client sees the alerts raised for their own estate.
    /// </summary>
    public bool ClientVisible { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Ordering in the sidebar; equal values fall back to the name.</summary>
    public int SortOrder { get; set; }

    /// <summary>Next ticket number on this board. Allocated under a transaction, never reused.</summary>
    public int NextNumber { get; set; } = 1;

    public Guid? CreatedByUserId { get; set; }

    public ICollection<BoardMember> Members { get; set; } = new List<BoardMember>();
    public ICollection<BoardTopic> Topics { get; set; } = new List<BoardTopic>();
}

/// <summary>
/// What a ticket on this board is about — "Patching", "Access request", "Site visit". Chosen when
/// the ticket is raised, and it fills in the answers that usually follow from it: which department
/// owns it, how urgent it starts, and who tends to get it.
///
/// A topic is a shortcut, never a rule: whoever raises the ticket can change anything it filled in.
/// </summary>
public class BoardTopic : TenantEntity
{
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }

    public required string Name { get; set; }

    /// <summary>The department this kind of work belongs to, when it always belongs to one.</summary>
    public Guid? DefaultDepartmentId { get; set; }

    /// <summary>Where this kind of work starts on the priority scale. Null keeps the board's default.</summary>
    public string? DefaultPriority { get; set; }

    /// <summary>Who usually picks this up. Null leaves the ticket for anyone.</summary>
    public Guid? DefaultAssigneeUserId { get; set; }

    /// <summary>Hours from raising to when it is due, when this kind of work has a usual deadline.</summary>
    public int? DueInHours { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// Membership of a board. A board with no members is open to every member of staff who may see
/// tickets at all — which is the normal case for a team that wants to see each other's work.
/// Adding members narrows it to those people, for a board that should not be general reading.
/// </summary>
public class BoardMember : TenantEntity
{
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }
    public Guid AppUserId { get; set; }
}

/// <summary>
/// One handover of a ticket: who passed it, to whom, who decided, and what they said. Written on
/// every assignment change so a shift handover reads as a history rather than a single current
/// value — the day team's last word is still there when the night team picks the ticket up.
/// </summary>
public class TicketAssignment : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>Who held it before this change, if anyone.</summary>
    public Guid? FromAppUserId { get; set; }

    /// <summary>Who holds it now. Null means it was returned to the board unassigned.</summary>
    public Guid? ToAppUserId { get; set; }

    /// <summary>Who made the change. Anyone on the team may assign to anyone, so this is not the same person.</summary>
    public Guid? AssignedByUserId { get; set; }

    /// <summary>What the handover said, if anything.</summary>
    public string? Note { get; set; }
}
