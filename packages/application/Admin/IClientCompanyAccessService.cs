namespace Desk.Application.Admin;

/// <summary>A company a client user can act in: their own, or one they were given.</summary>
/// <param name="IsOwn">Their own company. It needs no grant and is always first.</param>
/// <param name="SeesAllTickets">Every ticket of the company, or only the ones they raised.</param>
/// <param name="CanCreate">May raise tickets and reply there. False: the company is read-only to them.</param>
public sealed record MyCompanyDto(Guid Id, string Name, bool IsOwn, bool SeesAllTickets, bool CanCreate);

/// <summary>One further company a client user has been given, as the desk sees it.</summary>
public sealed record CompanyGrantDto(Guid CompanyId, string CompanyName, bool SeesAllTickets, bool CanCreate, DateTimeOffset GrantedAt);

public sealed record ClientUserAccessDto(
    Guid Id, string DisplayName, string Email, bool IsActive, bool HasSignedIn,
    Guid HomeCompanyId, string HomeCompanyName, IReadOnlyList<CompanyGrantDto> Grants);

public sealed record ClientCompanyChoiceDto(Guid Id, string Name);

/// <summary>What a grant gives. Both are off unless said: the least a grant can be is "sees the tickets they raised there, and changes nothing".</summary>
public sealed record SetCompanyGrantInput(bool SeesAllTickets = false, bool CanCreate = false);

/// <summary>
/// Which companies a client user can see into besides their own.
///
/// A grant is made here, by someone at the desk who manages users, for one person and one company
/// at a time, and by nothing else: nothing is worked out from an e-mail address, its domain or a
/// company's name. Every grant, change and removal is audited with who made it.
/// </summary>
public interface IClientCompanyAccessService
{
    /// <summary>The companies the signed-in client user can act in: their own first, then each they were given. Empty for anyone who is not a client user.</summary>
    Task<IReadOnlyList<MyCompanyDto>> MineAsync(string idpSubject, CancellationToken ct = default);

    /// <summary>Client users of the organization with the companies each has been given. At most <paramref name="take"/>, by name.</summary>
    Task<IReadOnlyList<ClientUserAccessDto>> ListUsersAsync(string? search, int take = 50, CancellationToken ct = default);

    Task<ClientUserAccessDto> GetUserAsync(Guid clientUserId, CancellationToken ct = default);

    /// <summary>The organization's client companies, to choose one to give.</summary>
    Task<IReadOnlyList<ClientCompanyChoiceDto>> CompaniesAsync(CancellationToken ct = default);

    /// <summary>Gives the person the company, or changes what the grant gives. Their own company cannot be given: it is theirs already.</summary>
    Task<ClientUserAccessDto> GrantAsync(Guid clientUserId, Guid clientCompanyId, SetCompanyGrantInput input, CancellationToken ct = default);

    /// <summary>Takes the company away. From the next request on they cannot see into it.</summary>
    Task<ClientUserAccessDto> RevokeAsync(Guid clientUserId, Guid clientCompanyId, CancellationToken ct = default);
}
