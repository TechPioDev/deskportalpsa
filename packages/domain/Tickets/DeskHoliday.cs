using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// A day the desk itself is closed — a public holiday, an office closure. An SLA plan that counts
/// working hours skips these days, so a ticket raised the evening before Diwali is not due on Diwali.
///
/// The desk's own calendar, distinct from the holidays a CLIENT records for their account in the
/// control panel: those say when the customer is closed, this says when the team is.
/// Round-the-clock plans ignore it — a 24x7 desk works holidays by definition.
/// </summary>
public class DeskHoliday : TenantEntity
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
}
