using Desk.Domain.Enums;
using Desk.Infrastructure.Connectors;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// What every connector FACTORY has to get right, as the connector suite is what every connector
/// has to. A factory is how a provider reaches the screens: its descriptor is the form an
/// administrator fills in, and its account key is what stops one PSA account being connected
/// twice. A new provider's factory is added to <see cref="Factories"/>, and has to pass.
/// </summary>
public class ConnectorFactoryCertificationTests
{
    // Built without a database or a network: the descriptor and the account key need neither.
    public static TheoryData<IConnectorFactory, string, Dictionary<string, string>> Factories() => new()
    {
        {
            new AutotaskConnectorFactory(null!, null!, null!, TimeProvider.System, null!),
            "https://webservices31.autotask.net/ATServicesRest/",
            new() { ["ApiIntegrationCode"] = "code-not-to-be-seen", ["UserName"] = "api@techpio.test", ["Secret"] = "secret-not-to-be-seen" }
        },
        {
            new ConnectWiseConnectorFactory(null!, null!, null!, TimeProvider.System, null!),
            "https://api-na.myconnectwise.net/v4_6_release/apis/3.0/",
            new() { ["CompanyId"] = "techpio", ["PublicKey"] = "public-not-to-be-seen", ["PrivateKey"] = "private-not-to-be-seen", ["ClientId"] = "client-not-to-be-seen" }
        },
    };

    [Theory, MemberData(nameof(Factories))]
    public void Its_descriptor_is_a_form_an_administrator_can_fill_in(IConnectorFactory factory, string endpoint, Dictionary<string, string> credentials)
    {
        var d = factory.Descriptor!;

        d.Should().NotBeNull("a factory with no descriptor is not offered to anyone");
        d.Provider.Should().Be(factory.Provider);
        d.Name.Should().NotBeNullOrWhiteSpace();
        d.EndpointExample.Should().StartWith("https://", "the example is what people copy");
        d.EndpointHint.Should().NotBeNullOrWhiteSpace();
        d.Credentials.Should().NotBeEmpty();
        d.Credentials.Select(c => c.Key).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(credentials.Keys,
            "the fields it asks for are the fields it reads");
        d.Credentials.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Label));
        d.Credentials.Should().Contain(c => c.Secret, "at least one of them is a secret, and is typed as one");
        endpoint.Should().StartWith("https://");
    }

    [Theory, MemberData(nameof(Factories))]
    public void Its_account_key_names_the_PSA_account_and_nothing_secret(IConnectorFactory factory, string endpoint, Dictionary<string, string> credentials)
    {
        var key = factory.AccountKey(endpoint, credentials);

        key.Should().NotBeNullOrWhiteSpace("without one the same account could be connected twice");
        foreach (var field in factory.Descriptor!.Credentials.Where(c => c.Secret))
            key.Should().NotContain(credentials[field.Key], "it is hashed and stored beside the connection: no secret belongs in it");

        // The same account, written another way.
        var shouted = new Dictionary<string, string>(credentials.ToDictionary(c => c.Key, c => c.Value.ToUpperInvariant()));
        factory.AccountKey(endpoint.ToUpperInvariant().Replace("HTTPS://", "https://"), shouted).Should().Be(key, "case is not a different account");
        factory.AccountKey(endpoint.TrimEnd('/'), credentials).Should().Be(key, "nor is a trailing slash");

        // Another host is another account.
        factory.AccountKey(endpoint.Replace("://", "://other-"), credentials).Should().NotBe(key);
        // Credentials it cannot make a key from: it says so, rather than inventing one.
        factory.AccountKey(endpoint, new Dictionary<string, string>()).Should().BeNull();
        factory.AccountKey("not an address", credentials).Should().BeNull();
    }

    [Fact]
    public void Every_provider_has_a_name_and_a_mark_and_no_two_marks_of_connectable_providers_are_the_same()
    {
        foreach (var provider in Enum.GetValues<ProviderType>())
        {
            ProviderNames.Short(provider).Should().NotBeNullOrWhiteSpace();
            ProviderNames.Mark(provider).Should().MatchRegex("^[A-Z0-9]{2}$", $"{provider} needs two letters for its tile");
        }
        new[] { ProviderType.AutotaskPsa, ProviderType.ConnectWisePsa }.Select(ProviderNames.Mark).Should().OnlyHaveUniqueItems();
        (ProviderNames.Short(ProviderType.AutotaskPsa), ProviderNames.Short(ProviderType.ConnectWisePsa)).Should().Be(("Autotask", "ConnectWise"),
            "these are in references people already know: Autotask 12345");
    }
}
