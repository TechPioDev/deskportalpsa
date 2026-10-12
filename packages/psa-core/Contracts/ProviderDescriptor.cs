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

/// <summary>
/// What a PSA is called where it is named in a sentence, a reference or a tile. In one place: it
/// was written out in three, and they did not agree about the ones with no connector yet.
/// </summary>
public static class ProviderNames
{
    /// <summary>The short name: "Autotask", as in "Autotask 12345" or "the Autotask connection".</summary>
    public static string Short(ProviderType provider) => provider switch
    {
        ProviderType.ConnectWisePsa => "ConnectWise",
        ProviderType.AutotaskPsa => "Autotask",
        ProviderType.HaloPsa => "HaloPSA",
        ProviderType.Syncro => "Syncro",
        ProviderType.SuperOps => "SuperOps",
        ProviderType.Atera => "Atera",
        ProviderType.KaseyaBms => "Kaseya BMS",
        ProviderType.NableMspManager => "N-able MSP Manager",
        ProviderType.DeskDay => "DeskDay",
        ProviderType.ServiceNow => "ServiceNow",
        ProviderType.Freshservice => "Freshservice",
        ProviderType.JiraServiceManagement => "Jira Service Management",
        ProviderType.ManageEngineServiceDeskPlus => "ManageEngine ServiceDesk Plus",
        ProviderType.Zendesk => "Zendesk",
        ProviderType.ZohoDesk => "Zoho Desk",
        _ => provider.ToString(),
    };

    /// <summary>
    /// Two letters for a connection's tile where no logo has been uploaded. Initials, not a vendor's
    /// logo: the portal ships no brand asset it has no licence for.
    /// </summary>
    public static string Mark(ProviderType provider) => provider switch
    {
        ProviderType.ConnectWisePsa => "CW",
        ProviderType.AutotaskPsa => "AT",
        _ => Initials(Short(provider)),
    };

    private static string Initials(string name)
    {
        var words = name.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name.Length >= 2 ? name[..2] : name;
        return letters.ToUpperInvariant();
    }
}
