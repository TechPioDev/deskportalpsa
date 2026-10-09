namespace Desk.Application.ControlPanel;

/// <summary>Section keys mirror <see cref="Desk.Domain.ControlPanel.ControlPanelSection"/> names (camelCase on the wire).</summary>
public sealed record ControlPanelCapabilities(
    bool IsCompanyAdministrator,
    Guid ClientCompanyId,
    string CompanyName,
    IReadOnlyList<string> Sections);

/// <summary>One instruction scope. <see cref="ClientCompanyId"/> null = the organization-wide default.</summary>
public sealed record InstructionDto(
    Guid? ClientCompanyId,
    string Scope,          // "global" | "account"
    string AccountName,
    string Body,
    string? LastEditedBy,
    DateTimeOffset? UpdatedAt);

public sealed record InstructionsView(
    InstructionDto Global,
    IReadOnlyList<InstructionDto> Accounts);

public sealed record AccessGrantDto(string Section, Guid? ClientCompanyId);

public sealed record ClientUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsCompanyAdministrator,
    bool IsActive,
    IReadOnlyList<AccessGrantDto> Grants,
    bool SignInLinked = false);

public sealed record InviteClientUserInput(
    string Email,
    string DisplayName,
    bool IsCompanyAdministrator);

public sealed record SetAccessInput(
    bool IsCompanyAdministrator,
    IReadOnlyList<AccessGrantDto> Grants);
