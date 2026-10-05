using System.Net;
using System.Net.Sockets;
using Desk.Application.Admin;
using Desk.Application.Attachments;
using Desk.Application.Common;
using Desk.Application.Connectors;
using Desk.Domain.Enums;
using Desk.Infrastructure;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Security;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Where a PSA connection may point, and what it may reach when it is used.
///
/// A connection's credentials go to its API address on every call. The address used to be saved as
/// typed, and the guard that refuses private addresses was opt-in - and the production worker, which
/// makes every scheduled call, had never been opted in.
/// </summary>
public class ConnectorEndpointTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // ---- what may be saved ---------------------------------------------------------------------

    [Theory]
    [InlineData(ProviderType.AutotaskPsa, "https://webservices31.autotask.net")]          // production today
    [InlineData(ProviderType.AutotaskPsa, "https://webservices5.autotask.net/ATServicesRest/v1.0")]
    [InlineData(ProviderType.ConnectWisePsa, "https://staging.connectwisedev.com")]       // production today
    [InlineData(ProviderType.ConnectWisePsa, "https://api-na.myconnectwise.net/v4_6_release/apis/3.0")]
    [InlineData(ProviderType.ConnectWisePsa, "https://psa.customer.example")]            // self-hosted, public
    public void A_real_PSA_address_is_accepted_as_typed(ProviderType provider, string endpoint)
        => ConnectorEndpointPolicy.Strict.Validate(provider, "  " + endpoint + " ").Should().Be(endpoint);

    [Theory]
    [InlineData("http://webservices31.autotask.net", "*https://*")]            // credentials in the clear
    [InlineData("https://user:secret@webservices31.autotask.net", "*credentials*")]
    [InlineData("https://webservices31.autotask.net/?x=1", "*cannot contain*")]
    [InlineData("ftp://webservices31.autotask.net", "*web address*")]
    [InlineData("webservices31.autotask.net", "*web address*")]
    [InlineData("", "*Enter*")]
    [InlineData("https://autotask.net.attacker.example", "*autotask.net*")]    // looks like it, is not
    [InlineData("https://webservices31-autotask.net", "*autotask.net*")]
    [InlineData("https://attacker.example", "*autotask.net*")]
    public void An_Autotask_connection_cannot_be_pointed_elsewhere(string endpoint, string because)
    {
        var act = () => ConnectorEndpointPolicy.Strict.Validate(ProviderType.AutotaskPsa, endpoint);

        act.Should().Throw<ValidationFailedException>().WithMessage(because);
    }

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://psa.localhost:8443")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://192.168.1.20/v4_6_release")]
    [InlineData("https://169.254.169.254")]                // cloud metadata
    [InlineData("https://[::1]")]
    [InlineData("https://[::ffff:10.0.0.5]")]              // IPv4 written as IPv6
    [InlineData("https://fileserver.local")]
    [InlineData("https://vault.internal")]
    public void A_private_address_is_refused_when_it_is_saved(string endpoint)
    {
        var act = () => ConnectorEndpointPolicy.Strict.Validate(ProviderType.ConnectWisePsa, endpoint);

        act.Should().Throw<ValidationFailedException>().WithMessage("*private*");
    }

    [Fact]
    public void A_host_the_operator_has_allowed_is_accepted_even_on_a_private_network()
    {
        var policy = new ConnectorEndpointPolicy(false, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "psa.corp.internal" });

        policy.Validate(ProviderType.ConnectWisePsa, "https://PSA.corp.internal/v4_6_release")
            .Should().Be("https://PSA.corp.internal/v4_6_release");
        // Allowing one host says nothing about plain http, or about any other host.
        ((Action)(() => policy.Validate(ProviderType.ConnectWisePsa, "http://psa.corp.internal"))).Should().Throw<ValidationFailedException>();
        ((Action)(() => policy.Validate(ProviderType.ConnectWisePsa, "https://other.corp.internal"))).Should().Throw<ValidationFailedException>();
    }

    [Fact]
    public void A_local_machine_may_use_a_fake_on_localhost()
    {
        var local = ConnectorEndpointPolicy.From(Config(("LocalMode:Enabled", "true")));

        local.Validate(ProviderType.AutotaskPsa, "http://localhost:5099").Should().Be("http://localhost:5099");
        // Still an address, and still without credentials in it.
        ((Action)(() => local.Validate(ProviderType.AutotaskPsa, "http://u:p@localhost:5099"))).Should().Throw<ValidationFailedException>();
    }

    // ---- the service ----------------------------------------------------------------------------

    private sealed class Resolver(params ProviderType[] supported) : IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default)
            => throw new ValidationFailedException("not reachable in this test");
        public bool Supports(ProviderType provider) => supported.Contains(provider);
    }

    /// <summary>Counts what is written, so a test can say that nothing was.</summary>
    private sealed class CountingSecrets(Desk.Application.Abstractions.ISecretStore inner) : Desk.Application.Abstractions.ISecretStore
    {
        public int Writes { get; private set; }
        public Task<string> WriteAsync(string logicalName, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        { Writes++; return inner.WriteAsync(logicalName, data, ct); }
        public Task<IReadOnlyDictionary<string, string>> ReadAsync(string secretRef, CancellationToken ct = default) => inner.ReadAsync(secretRef, ct);
        public Task<string> RotateAsync(string secretRef, IReadOnlyDictionary<string, string> data, CancellationToken ct = default)
        { Writes++; return inner.RotateAsync(secretRef, data, ct); }
        public Task DeleteAsync(string secretRef, CancellationToken ct = default) => inner.DeleteAsync(secretRef, ct);
    }

    private static (ConnectionAdminService Service, AdminHarness Harness, CountingSecrets Secrets) Service()
    {
        var h = AdminHarness.Create(Org);
        var secrets = new CountingSecrets(h.Secrets);
        var service = new ConnectionAdminService(h.Db, secrets, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock),
            new Resolver(ProviderType.AutotaskPsa, ProviderType.ConnectWisePsa), new ConnectionFieldCache(),
            new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), h.Clock);
        return (service, h, secrets);
    }

    private static readonly Dictionary<string, string> Keys = new() { ["UserName"] = "u", ["Secret"] = "s" };

    [Fact]
    public async Task A_connection_to_a_refused_address_is_not_created_and_its_credentials_are_not_stored()
    {
        var (service, h, secrets) = Service();
        await using var _ = h.Db;

        var act = () => service.CreateAsync(new CreateConnectionInput(
            "AT", ProviderType.AutotaskPsa, "https://attacker.example", null, Keys, null));

        await act.Should().ThrowAsync<ValidationFailedException>();
        (await h.Db.PsaConnections.CountAsync()).Should().Be(0);
        secrets.Writes.Should().Be(0, "the credentials were sent with the request; refusing it must not keep them");
    }

    [Fact]
    public async Task An_edit_to_a_refused_address_changes_nothing()
    {
        var (service, h, secrets) = Service();
        await using var _ = h.Db;
        var created = await service.CreateAsync(new CreateConnectionInput(
            "AT", ProviderType.AutotaskPsa, "https://webservices31.autotask.net", null, Keys, null));
        var writesBefore = secrets.Writes;

        var act = () => service.UpdateAsync(created.Id, new UpdateConnectionInput(
            "Renamed", "https://169.254.169.254", null, null, true,
            new Dictionary<string, string> { ["Secret"] = "rotated" }));

        await act.Should().ThrowAsync<ValidationFailedException>();
        var row = await h.Db.PsaConnections.AsNoTracking().SingleAsync();
        row.Name.Should().Be("AT");
        row.ApiEndpoint.Should().Be("https://webservices31.autotask.net");
        secrets.Writes.Should().Be(writesBefore);
        (await h.Secrets.ReadAsync(row.CredentialSecretRef))["Secret"].Should().Be("s", "a refused edit must not rotate the key either");
    }

    [Fact]
    public async Task A_PSA_with_no_connector_cannot_be_connected()
    {
        // The catalog names PSAs that are planned. A connection to one would sit enabled and fail
        // every poll, for a reason no administrator could fix.
        var (service, h, secrets) = Service();
        await using var _ = h.Db;

        var act = () => service.CreateAsync(new CreateConnectionInput(
            "Halo", ProviderType.HaloPsa, "https://halo.example", null, Keys, null));

        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*cannot be connected yet*");
        (await h.Db.PsaConnections.CountAsync()).Should().Be(0);
        secrets.Writes.Should().Be(0);
    }

    // ---- what a connector may reach -------------------------------------------------------------

    [Theory]
    [InlineData("::ffff:10.0.0.5")]       // IPv4 written as IPv6
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:5")]        // NAT64 carrying 10.0.0.5
    [InlineData("2002:c0a8:101::1")]      // 6to4 carrying 192.168.1.1
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]             // multicast
    [InlineData("255.255.255.255")]       // broadcast
    [InlineData("198.18.0.1")]            // benchmarking
    [InlineData("ff02::1")]               // IPv6 multicast
    public void An_address_that_leads_to_a_private_one_is_blocked(string ip)
        => EgressGuard.IsBlockedAddress(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]      // NAT64 carrying 8.8.8.8
    [InlineData("198.17.0.1")]
    [InlineData("223.255.255.1")]
    public void A_public_address_in_another_notation_is_still_public(string ip)
        => EgressGuard.IsBlockedAddress(IPAddress.Parse(ip)).Should().BeFalse();

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void The_guard_is_on_unless_this_is_local_mode_or_the_operator_says_otherwise()
    {
        // The production worker sets nothing: that has to mean ON. It used to mean off.
        EgressGuard.IsEnabled(Config()).Should().BeTrue();
        EgressGuard.IsEnabled(Config(("LocalMode:Enabled", "true"))).Should().BeFalse();
        EgressGuard.IsEnabled(Config(("Connectors:BlockPrivateEgress", "false"))).Should().BeFalse();
        EgressGuard.IsEnabled(Config(("LocalMode:Enabled", "true"), ("Connectors:BlockPrivateEgress", "true"))).Should().BeTrue();
    }

    /// <summary>A real listener on this machine, standing in for "something on the server's own network".</summary>
    private sealed class LocalServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public int Connections;

        public LocalServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref Connections);
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
                }
            }
            catch (Exception) { /* listener stopped */ }
        }

        public void Dispose() => _listener.Stop();
    }

    private static HttpClient GuardedClient(params string[] allowedHosts)
    {
        var allowed = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        return new HttpClient(new EgressGuard(allowed) { InnerHandler = EgressGuard.PinnedHandler(allowed) });
    }

    [Fact]
    public async Task A_guarded_client_does_not_reach_an_address_on_this_machine()
    {
        using var server = new LocalServer();
        using var client = GuardedClient();

        var act = () => client.GetAsync($"http://127.0.0.1:{server.Port}/");

        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.InvalidRequest);
        server.Connections.Should().Be(0, "refused before a socket is opened, so nothing was sent");
    }

    [Fact]
    public async Task The_connection_itself_is_checked_not_only_the_request()
    {
        // The transport alone, without the handler in front of it: what stands between a name that
        // resolved to a public address when it was checked and a private one when it is connected.
        using var server = new LocalServer();
        using var client = new HttpClient(EgressGuard.PinnedHandler(new HashSet<string>()));

        var act = () => client.GetAsync($"http://127.0.0.1:{server.Port}/");

        await act.Should().ThrowAsync<Exception>();
        server.Connections.Should().Be(0);
    }

    [Fact]
    public async Task A_host_the_operator_allowed_is_reached()
    {
        using var server = new LocalServer();
        using var client = GuardedClient("127.0.0.1");

        var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        server.Connections.Should().Be(1);
    }

    [Theory]
    [InlineData("autotask")]
    [InlineData("connectwise")]
    public async Task A_process_that_sets_nothing_gets_guarded_connector_clients(string clientName)
    {
        // The registration the worker runs. Asserted through behaviour: the named client a
        // connector factory asks for must refuse an address on this machine.
        using var server = new LocalServer();
        var services = new ServiceCollection();
        services.AddDeskInfrastructure(Config(
            ("ConnectionStrings:Postgres", "Host=unused"),
            ("Secrets:EncryptionKey", Convert.ToBase64String(new byte[32]))));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        var act = () => client.GetAsync($"http://127.0.0.1:{server.Port}/");

        await act.Should().ThrowAsync<ConnectorException>();
        server.Connections.Should().Be(0);
    }
}
