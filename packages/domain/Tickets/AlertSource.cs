using Desk.Domain.Common;

namespace Desk.Domain.Tickets;

/// <summary>
/// One monitoring tool allowed to open tickets on a board: a NinjaOne or Datto RMM webhook, or
/// anything else that can post JSON.
///
/// Each source has its own key. The key is never stored — only a hash of it — so a copy of the
/// database gives nobody the ability to raise tickets here, and a leaked key is replaced by issuing
/// a new one rather than by reading the old one back.
/// </summary>
public class AlertSource : TenantEntity
{
    public required string Name { get; set; }

    /// <summary>The board its alerts land on. Deleting is not offered; a source is switched off instead.</summary>
    public Guid BoardId { get; set; }
    public Board? Board { get; set; }

    /// <summary>Which tool this is, for the payload shapes it is known to send.</summary>
    public AlertVendor Vendor { get; set; } = AlertVendor.Generic;

    /// <summary>SHA-256 of the key, Base64. The key itself is shown once, at creation, and never again.</summary>
    public required string KeyHash { get; set; }

    /// <summary>First characters of the key, so a person can tell two sources apart without the secret.</summary>
    public required string KeyHint { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Close the ticket when the tool says the alert cleared. On by default: a monitoring ticket that
    /// stays open after the condition resolved is noise that trains people to ignore the board.
    /// </summary>
    public bool CloseOnClear { get; set; } = true;

    public DateTimeOffset? LastReceivedAt { get; set; }
    public int ReceivedCount { get; set; }

    /// <summary>Why the last delivery was refused, if it was. Shown to whoever is setting the tool up.</summary>
    public string? LastError { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

public enum AlertVendor
{
    /// <summary>Anything that can post the documented JSON.</summary>
    Generic = 0,
    NinjaOne = 1,
    DattoRmm = 2,
}
