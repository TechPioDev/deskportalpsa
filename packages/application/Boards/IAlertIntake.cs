using Desk.Domain.Tickets;

namespace Desk.Application.Boards;

/// <summary>
/// One alert as the portal understands it, whatever shape the tool sent.
/// </summary>
/// <param name="AlertId">
/// The tool's own id for the condition. The same id arriving twice updates one ticket rather than
/// opening a second, and is what a "cleared" message closes.
/// </param>
/// <param name="Cleared">The condition has resolved. The ticket closes itself if the source says to.</param>
/// <param name="Client">
/// The customer name as the tool knows it. Matched against the client companies the portal already
/// holds; an unrecognised name is kept on the ticket rather than guessed at.
/// </param>
public sealed record AlertMessage(
    string AlertId,
    string Title,
    string? Description = null,
    string? Severity = null,
    string? Device = null,
    string? Client = null,
    bool Cleared = false,
    DateTimeOffset? OccurredAt = null);

/// <param name="Outcome">"opened", "updated", "closed" or "ignored", in the words the log uses.</param>
public sealed record AlertResult(string Outcome, Guid? TicketId, string? Number, string? Detail = null);

/// <summary>
/// Receiving alerts from a monitoring tool. Authentication is the source's own key and nothing else:
/// the request arrives from a vendor's cloud, so there is no session and no user behind it.
/// </summary>
public interface IAlertIntakeService
{
    /// <summary>
    /// Handles one delivery. An unknown or inactive key is reported as not found rather than
    /// unauthorised, so a caller probing keys learns nothing from the difference.
    /// </summary>
    Task<AlertResult> ReceiveAsync(string key, AlertMessage message, CancellationToken ct = default);
}

/// <param name="Key">
/// Shown once, at creation, and never again — only its hash is kept. A lost key is replaced, not recovered.
/// </param>
public sealed record AlertSourceCreatedDto(AlertSourceDto Source, string Key);

public sealed record AlertSourceDto(
    Guid Id, string Name, Guid BoardId, string BoardName, AlertVendor Vendor, string KeyHint,
    bool IsActive, bool CloseOnClear, DateTimeOffset? LastReceivedAt, int ReceivedCount, string? LastError);

public sealed record AlertSourceInput(string Name, Guid BoardId, AlertVendor Vendor = AlertVendor.Generic, bool CloseOnClear = true);

/// <summary>Managing which tools may open tickets. A lead's decision, like the boards themselves.</summary>
public interface IAlertSourceService
{
    Task<IReadOnlyList<AlertSourceDto>> ListAsync(CancellationToken ct = default);
    Task<AlertSourceCreatedDto> CreateAsync(AlertSourceInput input, CancellationToken ct = default);
    Task<AlertSourceDto> UpdateAsync(Guid id, AlertSourceInput input, CancellationToken ct = default);
    Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default);

    /// <summary>Issues a new key and invalidates the old one, for a key that leaked or was lost.</summary>
    Task<AlertSourceCreatedDto> RegenerateKeyAsync(Guid id, CancellationToken ct = default);
}
