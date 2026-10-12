namespace Desk.Application.Admin;

/// <summary>
/// One of a PSA's custom fields, with what has been decided about it on this connection.
/// </summary>
/// <param name="Key">How the PSA knows the field. What a decision is saved against.</param>
/// <param name="Label">What the PSA's own screen calls it.</param>
/// <param name="DataType">text, number, date, boolean or list.</param>
/// <param name="ListedByPsa">The PSA lists it now. False: a decision is saved for a field the PSA no longer lists, or could not be asked.</param>
/// <param name="Import">Brought in. False, the default, is ignored: nothing of it is stored.</param>
/// <param name="PortalLabel">What it is called in the portal.</param>
/// <param name="ClientVisible">The client may see it too. False, the default, is staff only.</param>
/// <param name="Editable">Whether its value can be changed from the portal. Always false: see <paramref name="EditableReason"/>.</param>
public sealed record CustomFieldDto(
    string Key, string Label, string DataType, bool ListedByPsa,
    bool Import, string PortalLabel, bool ClientVisible, bool Editable, string EditableReason);

public sealed record CustomFieldSettingsDto(
    Guid ConnectionId, string ConnectionName, string Provider,
    /// <summary>Whether this PSA's connector reads custom fields at all.</summary>
    bool Supported,
    IReadOnlyList<CustomFieldDto> Fields,
    int Imported, int ClientVisible,
    IReadOnlyList<string> Notes);

/// <summary>
/// Asks a connection's PSA which custom fields it has. Kept apart from the service that decides
/// about them so that a connection still being set up can be asked the way the setup wizard asks
/// it: fields are chosen before the first import, not after it.
/// </summary>
public interface ICustomFieldSource
{
    /// <summary>
    /// Whether the connection's connector reads custom fields, and the fields its PSA lists now.
    /// Throws where the PSA cannot be asked (not answering, or the connection is switched off).
    /// </summary>
    Task<(bool Supported, IReadOnlyList<Desk.PsaCore.Models.ExternalFieldDefinition> Fields)> ListCustomFieldsAsync(Guid connectionId, CancellationToken ct = default);
}

/// <summary>A decision about one field. A field left out of a save keeps what was decided before.</summary>
public sealed record SetCustomFieldInput(string Key, bool Import, string? PortalLabel = null, bool ClientVisible = false);

/// <summary>
/// Which of a connection's PSA custom fields the portal brings in, what each is called here and
/// who may see it. Nothing is brought in until it is chosen, and a chosen field is for staff only
/// until it is separately said to be the client's to see. Reading needs mappings.view, changing
/// mappings.manage; every change is audited, and showing a field to clients is named in the audit.
/// </summary>
public interface ICustomFieldService
{
    Task<CustomFieldSettingsDto> GetAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Saves decisions about some fields. Everything wrong is said together and nothing is saved
    /// unless all of it is right. Tickets already here are given or lose a field's value the next
    /// time each is read; what a ticket shows changes at once, because that is decided when it is read.
    /// </summary>
    Task<CustomFieldSettingsDto> SaveAsync(Guid connectionId, IReadOnlyList<SetCustomFieldInput> changes, CancellationToken ct = default);
}
