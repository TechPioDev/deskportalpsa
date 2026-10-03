using Desk.Domain.Common;
using Desk.Domain.Tickets;

namespace Desk.Domain.Workforce;

/// <summary>
/// What planning a piece of work needs to know that the ticket itself does not say: how much
/// effort it takes, the window it may be placed in, whether it may be split across periods, and a
/// skill it asks for. One row per ticket, written by whoever plans; INTERNAL ONLY, never part of
/// what a client receives (it lives beside the ticket, not on it).
///
/// Effort and calendar placement are kept apart: this row says how much and within what window;
/// the allocations say when. What is allocated and what remains are derived from the allocations,
/// never stored here.
/// </summary>
public sealed class WorkPlanning : TenantEntity
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    /// <summary>The effort the work is expected to take, in minutes; null when nobody has estimated it.</summary>
    public int? RequiredMinutes { get; set; }
    /// <summary>The earliest the work may start and the latest it must be finished by; null = unconstrained.</summary>
    public DateTimeOffset? EarliestStart { get; set; }
    public DateTimeOffset? LatestEnd { get; set; }
    /// <summary>Whether the effort may be done in several periods. Off by default: most work wants one sitting.</summary>
    public bool Splittable { get; set; }
    /// <summary>A skill the work asks for; placing it on someone without it is a warning, not a refusal.</summary>
    public Guid? RequiredSkillId { get; set; }
    /// <summary>A planning note for schedulers (300 chars); never shown to a client.</summary>
    public string? Note { get; set; }

    public Guid UpdatedByUserId { get; set; }
}
