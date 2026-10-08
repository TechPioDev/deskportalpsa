using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Desk.Application.Common;
using Desk.Application.Identity;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Identity;

/// <summary>Where the identity provider is and how the portal is allowed to manage its users.</summary>
public sealed class KeycloakAdminOptions
{
    /// <summary>The realm's issuer, as the API already validates tokens against ("https://host/realms/desk").</summary>
    public string? Authority { get; init; }
    /// <summary>A confidential client of the realm with a service account that holds realm-management's manage-users role.</summary>
    public string ClientId { get; init; } = "desk-admin";
    public string? ClientSecret { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>".../realms/desk" becomes ".../admin/realms/desk".</summary>
    public string AdminBase
    {
        get
        {
            var a = (Authority ?? "").TrimEnd('/');
            var i = a.LastIndexOf("/realms/", StringComparison.Ordinal);
            return i < 0 ? a : a[..i] + "/admin" + a[i..];
        }
    }
}

/// <summary>
/// The portal's hand in the identity provider: one user made with a password, one password set.
/// A token is fetched for each call with the client's own credentials; nothing is cached, since
/// these calls are rare (a person accepting an invitation) and a cached token is one more secret
/// in memory.
/// </summary>
public sealed class KeycloakAdminClient(IHttpClientFactory http, KeycloakAdminOptions options, ILogger<KeycloakAdminClient> logger) : IKeycloakAdmin
{
    public const string HttpClientName = "keycloak-admin";

    public bool IsConfigured => options.IsConfigured;

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
    private sealed record KeycloakUser([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("email")] string? Email);

    private async Task<HttpClient> ClientAsync(CancellationToken ct)
    {
        if (!options.IsConfigured)
            throw new ValidationFailedException("The sign-in service is not set up to accept invitations yet. Ask the portal's host to configure the Keycloak admin client.");
        var client = http.CreateClient(HttpClientName);
        using var tokenResponse = await client.PostAsync($"{options.Authority!.TrimEnd('/')}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret!,
            }), ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogError("Keycloak admin token refused: {Status}", (int)tokenResponse.StatusCode);
            throw new ValidationFailedException("The sign-in service refused the portal's credentials. The Keycloak admin client needs attention.");
        }
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new ValidationFailedException("The sign-in service answered without a token.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    public async Task<string?> FindUserIdByEmailAsync(string email, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        var users = await client.GetFromJsonAsync<List<KeycloakUser>>(
            $"{options.AdminBase}/users?email={Uri.EscapeDataString(email)}&exact=true", ct) ?? [];
        return users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public async Task<string> CreateUserAsync(string email, string displayName, string password, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        var (first, last) = SplitName(displayName);
        using var response = await client.PostAsJsonAsync($"{options.AdminBase}/users", new
        {
            username = email, email, firstName = first, lastName = last, enabled = true, emailVerified = true,
            credentials = new[] { new { type = "password", value = password, temporary = false } },
        }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Made earlier (by hand, or by a first try whose answer was lost): the person is still
            // the one holding the link, so the password they chose now is set on that account.
            var existing = await FindUserIdByEmailAsync(email, ct)
                ?? throw new ValidationFailedException("The sign-in service already has a user it would not show for this address.");
            await SetPasswordAsync(existing, password, ct);
            return existing;
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Keycloak refused to create a user: {Status} {Body}", (int)response.StatusCode, await Safe(response, ct));
            throw new ValidationFailedException(response.StatusCode == HttpStatusCode.BadRequest
                ? "The sign-in service did not accept that password. Choose a longer one with letters and numbers."
                : "The sign-in service could not create the account. Try again in a minute.");
        }
        var location = response.Headers.Location?.ToString() ?? "";
        var id = location[(location.LastIndexOf('/') + 1)..];
        if (id.Length == 0)
            id = await FindUserIdByEmailAsync(email, ct) ?? throw new ValidationFailedException("The sign-in service created the account but did not say which.");
        return id;
    }

    public async Task SetPasswordAsync(string userId, string password, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        using var response = await client.PutAsJsonAsync($"{options.AdminBase}/users/{Uri.EscapeDataString(userId)}/reset-password",
            new { type = "password", value = password, temporary = false }, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Keycloak refused to set a password: {Status} {Body}", (int)response.StatusCode, await Safe(response, ct));
            throw new ValidationFailedException(response.StatusCode == HttpStatusCode.BadRequest
                ? "The sign-in service did not accept that password. Choose a longer one with letters and numbers."
                : "The sign-in service could not set the password. Try again in a minute.");
        }
    }

    private static async Task<string> Safe(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    public static (string First, string Last) SplitName(string displayName)
    {
        var parts = (displayName ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => ("", ""), 1 => (parts[0], ""), _ => (parts[0], parts[1]) };
    }
}

/// <summary>Local mode and tests: users live in memory, nothing is called.</summary>
public sealed class InMemoryKeycloakAdmin : IKeycloakAdmin
{
    private readonly Dictionary<string, (string Id, string Password)> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    public bool IsConfigured => true;
    public IReadOnlyDictionary<string, (string Id, string Password)> Users => _byEmail;

    public Task<string?> FindUserIdByEmailAsync(string email, CancellationToken ct = default)
        => Task.FromResult(_byEmail.TryGetValue(email, out var u) ? u.Id : null);

    public Task<string> CreateUserAsync(string email, string displayName, string password, CancellationToken ct = default)
    {
        if (_byEmail.TryGetValue(email, out var existing))
        {
            _byEmail[email] = (existing.Id, password);
            return Task.FromResult(existing.Id);
        }
        var id = Guid.NewGuid().ToString();
        _byEmail[email] = (id, password);
        return Task.FromResult(id);
    }

    public Task SetPasswordAsync(string userId, string password, CancellationToken ct = default)
    {
        var entry = _byEmail.FirstOrDefault(e => e.Value.Id == userId);
        if (entry.Key is null) throw new ValidationFailedException("No such user at the sign-in service.");
        _byEmail[entry.Key] = (userId, password);
        return Task.CompletedTask;
    }
}
