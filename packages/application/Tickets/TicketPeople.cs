namespace Desk.Application.Tickets;

/// <summary>
/// One identity per person across the portal and the PSA, as a string both halves of the UI can
/// compare: "u:{portal user id}" where the person has a portal account, "x:{provider resource id}"
/// only where they do not.
///
/// Portal-first for the reason the technician pages group that way: a portal-only technician's
/// tickets and time reach the PSA under the integration account, so the provider id would name every
/// one of them "the API user". Minted here, once, because Client workload's People list links to the
/// ticket list with this key - two formatters that disagreed on case or spacing would send the link
/// to an empty list.
/// </summary>
public static class PersonKey
{
    public static string For(Guid? appUserId, string? externalId)
        => appUserId is { } u ? "u:" + u.ToString("D") : "x:" + (externalId ?? "").Trim().ToLowerInvariant();
}

/// <summary>Someone who worked a ticket: its holder (<paramref name="Holds"/>), or someone who logged time on it.</summary>
public sealed record TicketPersonRef(string Key, string Name, bool Holds);
