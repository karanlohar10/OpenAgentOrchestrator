using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding;

/// <summary>
/// Acquires and caches OAuth2 client-credentials tokens for MCP server authentication.
/// </summary>
public sealed class ClientCredentialsTokenService : ITokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClientCredentialsTokenService> _logger;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    public ClientCredentialsTokenService(
        IHttpClientFactory httpClientFactory,
        ILogger<ClientCredentialsTokenService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var cacheKey = $"{tokenEndpoint}|{clientId}|{scope}";

        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.AccessToken;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Acquiring client-credentials token from {TokenEndpoint} for client {ClientId}",
                tokenEndpoint, clientId);
        }

        var token = await RequestTokenAsync(tokenEndpoint, clientId, clientSecret, scope, cancellationToken);
        _cache[cacheKey] = token;

        return token.AccessToken;
    }

    private async Task<CachedToken> RequestTokenAsync(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient("TokenService");

        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        };

        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters["scope"] = scope;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Token request to {TokenEndpoint} failed with {StatusCode}: {Error}",
                tokenEndpoint, response.StatusCode, errorBody);
            throw new InvalidOperationException(
                $"Failed to acquire token from {tokenEndpoint}: {response.StatusCode}");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Token endpoint returned null response.");

        // Cache with a 60-second safety margin before actual expiry
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);

        return new CachedToken(tokenResponse.AccessToken, expiresAt);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }
}
