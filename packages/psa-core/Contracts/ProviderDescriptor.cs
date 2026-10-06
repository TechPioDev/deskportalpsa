using Desk.Domain.Enums;

namespace Desk.PsaCore.Contracts;

/// <summary>One credential a connection to a provider is set up with.</summary>
/// <param name="Key">The name it is stored under and read back by.</param>
/// <param name="Label">What an administrator is shown.</param>
/// <param name="Secret">Entered as a password field. Every credential is stored encrypted and none is ever returned; this only decides how it is typed.</param>
/// <param name="Hint">One line saying where the value comes from, when that is not obvious.</param>
public sealed record CredentialField(string Key, string Label, bool Secret, string? Hint = null);

/// <summary>
/// What an administrator has to know to connect a PSA: what it is called, what its address looks
/// like, and which credentials it takes. Declared by the provider's connector factory, so the
/// screens ask the server instead of carrying a copy - the provider list and each provider's
/// credential field names used to be written out in the page, and a new connector meant editing it.
/// </summary>
public sealed record ProviderDescriptor(
    ProviderType Provider,
    string Name,
    string EndpointExample,
    string EndpointHint,
    IReadOnlyList<CredentialField> Credentials)
{
    /// <summary>The label of the optional identifier kept beside the address, where the provider has one worth naming.</summary>
    public string? TenantIdentifierLabel { get; init; }
}
