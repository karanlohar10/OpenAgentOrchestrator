using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    /// <summary>
    /// Binds Model Context Protocol tools - the only tool type currently supported by configured
    /// orchestrator workflows (e.g. against an openEHR MCP server).
    /// Supports both API Key and OAuth2 bearer token authentication.
    /// </summary>
    public sealed class McpToolBinder : IToolBinder
    {
        private readonly ITokenService _tokenService;

        public McpToolBinder(ITokenService tokenService)
        {
            _tokenService = tokenService;
        }

        public string SupportedType => "mcp";

        public async Task<IList<AITool>> BindAsync(ToolDefinition definition, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Endpoint);

            var headers = await ResolveHeadersAsync(definition, cancellationToken);

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(definition.Endpoint),
                Name = definition.Name,
                AdditionalHeaders = headers
            });

            var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            var toolsResult = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var tools = toolsResult.ToList();
            var filtered = tools.Where(t => t.Name == definition.Name).ToList();
            return filtered.Select(t => (AITool)t).ToList();
        }

        private async Task<Dictionary<string, string>?> ResolveHeadersAsync(
            ToolDefinition definition,
            CancellationToken cancellationToken)
        {
            if (string.Equals(definition.AuthType, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.TokenEndpoint);
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.ClientId);
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.ClientSecret);

                var accessToken = await _tokenService.GetAccessTokenAsync(
                    definition.TokenEndpoint,
                    definition.ClientId,
                    definition.ClientSecret,
                    definition.Scope,
                    cancellationToken);

                var headers = definition.Headers != null
                    ? new Dictionary<string, string>(definition.Headers)
                    : new Dictionary<string, string>();

                headers["Authorization"] = $"Bearer {accessToken}";
                return headers;
            }

            // Default: apiKey - use the literal Headers configured directly in config.yaml
            return definition.Headers;
        }
    }
}
