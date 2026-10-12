namespace Desk.Application.Boards;

/// <summary>A client the team's own ticket can be raised for. <paramref name="Local"/>: added by hand, in no PSA.</summary>
public sealed record ClientChoiceDto(Guid Id, string Name, bool Local);

/// <summary>A client added by hand, as the people who manage the boards see it.</summary>
/// <param name="Tickets">How many tickets name it. It cannot be removed while any does, only retired.</param>
public sealed record LocalClientDto(Guid Id, string Name, bool IsActive, int Tickets, DateTimeOffset AddedAt);

public sealed record LocalClientInput(string Name, bool IsActive = true);

/// <summary>
/// Clients that are in no PSA. A desk also works for people it has not set up in its PSA (a
/// prospect, a one-off job, a client of a sister company), and the team's own tickets should be
/// able to say who the work was for. Such a client is a name the desk keeps: nothing about it is
/// read from or written to a PSA, it has no client sign-ins, and no client is shown anything
/// because of it.
/// </summary>
public interface ILocalClientService
{
    /// <summary>Every client a ticket on a board can name: the PSA's own and the ones added by hand. Active ones only.</summary>
    Task<IReadOnlyList<ClientChoiceDto>> ChoicesAsync(CancellationToken ct = default);

    /// <summary>The clients added by hand, retired ones included.</summary>
    Task<IReadOnlyList<LocalClientDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Adds a client by name. A name already in the list, from a PSA or by hand, is refused with where it is.</summary>
    Task<LocalClientDto> AddAsync(LocalClientInput input, CancellationToken ct = default);

    /// <summary>Renames, retires or brings back a client added by hand. A PSA's client is not found here: its name is the PSA's.</summary>
    Task<LocalClientDto> UpdateAsync(Guid id, LocalClientInput input, CancellationToken ct = default);
}
