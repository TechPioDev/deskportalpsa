namespace Desk.Application.Tickets;

/// <summary>
/// One identity per person across the portal and the PSA, as a string both halves of the UI can
/// compare: "u:{portal user id}" where the person has a portal account, and
/// "x:{connection}:{provider resource id}" only where they do not.
///
/// Portal-first for the reason the technician pages group that way: a portal-only technician's
/// tickets and time reach the PSA under the integration account, so the provider id would name every
/// one of them "the API user". Minted here, once, because Client workload's People list links to the
/// ticket list with this key - two formatters that disagreed on case or spacing would send the link
/// to an empty list.
///
/// The connection is part of a PSA login's key because the id alone is not a person: resource 42 in
/// one PSA account and resource 42 in another are two people, and keyed by the id they were added
/// together into one. A key with no connection ("x:{id}") is one written before this - an old saved
/// view - and is still read.
/// </summary>
public static class PersonKey
{
    public static string For(Guid? appUserId, Guid? connectionId, string? externalId)
        => appUserId is { } u ? "u:" + u.ToString("D")
            : connectionId is { } c ? $"x:{c:N}:{Normal(externalId)}"
            : "x:" + Normal(externalId);

    /// <summary>
    /// Reads a PSA login's key. <paramref name="connectionId"/> is null for a key from before keys
    /// named their connection: such a key means that login wherever it is found.
    /// </summary>
    public static bool TryParseExternal(string? key, out Guid? connectionId, out string externalId)
    {
        connectionId = null;
        externalId = "";
        if (key is null || !key.StartsWith("x:", StringComparison.Ordinal)) return false;
        var rest = key[2..];
        if (rest.Length > 33 && rest[32] == ':' && Guid.TryParseExact(rest[..32], "N", out var connection))
        {
            connectionId = connection;
            rest = rest[33..];
        }
        externalId = Normal(rest);
        return externalId.Length > 0;
    }

    private static string Normal(string? externalId) => (externalId ?? "").Trim().ToLowerInvariant();
}

/// <summary>Someone who worked a ticket: its holder (<paramref name="Holds"/>), or someone who logged time on it.</summary>
public sealed record TicketPersonRef(string Key, string Name, bool Holds);
