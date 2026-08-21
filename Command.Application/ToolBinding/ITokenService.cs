namespace OpenAgentOrchestrator.Command.Application.ToolBinding;

/// <summary>
/// Acquires OAuth2 access tokens via the client-credentials flow for service-to-service
/// communication with MCP servers that require bearer authentication.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Gets a valid access token for the specified token endpoint and client credentials.
    /// Tokens are cached until near-expiry and refreshed automatically.
    /// </summary>
    Task<string> GetAccessTokenAsync(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope = null,
        CancellationToken cancellationToken = default);
}
