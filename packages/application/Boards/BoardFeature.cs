namespace Desk.Application.Boards;

/// <summary>
/// Whether internal boards are switched on for this installation.
///
/// One switch over the whole feature, so it can be turned off in production without a deploy and
/// without touching the database: the endpoints refuse, the pages disappear, and every ticket
/// already raised on a board stays exactly where it is, readable again the moment it is switched
/// back on. Nothing is deleted by turning it off.
///
/// Configuration: <c>Features:InternalBoards</c> (environment <c>Features__InternalBoards</c>).
/// Default on, because an installation that has boards needs them to keep working after an upgrade.
/// </summary>
public sealed class BoardFeatureOptions
{
    public bool InternalBoards { get; init; } = true;
}
