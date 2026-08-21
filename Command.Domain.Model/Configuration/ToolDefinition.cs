namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures a tool an agent can call. Only the "mcp" type is currently supported - the
    /// orchestrator workflows configured in this service exclusively use Model Context Protocol
    /// tools hosted by an MCP server.
    /// </summary>
    public sealed class ToolDefinition
    {
        /// <summary>Currently only "mcp" is supported.</summary>
        public required string Type { get; set; }

        /// <summary>For MCP tools, this must match the remote tool name.</summary>
        public required string Name { get; set; }

        public string? Endpoint { get; set; }

        /// <summary>
        /// Authentication type for the MCP server. Defaults to "apiKey" for backward compatibility.
        /// Supported values: "apiKey" (uses <see cref="Headers"/>), "bearer" (uses OAuth2
        /// client-credentials flow via <see cref="TokenEndpoint"/>, <see cref="ClientId"/>,
        /// <see cref="ClientSecret"/>, and <see cref="Scope"/>).
        /// </summary>
        public string AuthType { get; set; } = "apiKey";

        /// <summary>
        /// OAuth2 token endpoint URL for client-credentials flow (required when
        /// <see cref="AuthType"/> is "bearer").
        /// </summary>
        public string? TokenEndpoint { get; set; }

        /// <summary>
        /// OAuth2 client ID for client-credentials flow (required when
        /// <see cref="AuthType"/> is "bearer").
        /// </summary>
        public string? ClientId { get; set; }

        /// <summary>
        /// OAuth2 client secret for client-credentials flow, stored directly in
        /// <c>config.yaml</c> (required when <see cref="AuthType"/> is "bearer"). <c>config.yaml</c>
        /// is gitignored - never commit real values here.
        /// </summary>
        public string? ClientSecret { get; set; }

        /// <summary>
        /// OAuth2 scope to request when obtaining a bearer token. Space-separated list of scopes.
        /// </summary>
        public string? Scope { get; set; }

        /// <summary>
        /// Header name (e.g. "X-API-Key") to literal header value map, stored directly in
        /// <c>config.yaml</c> when <see cref="AuthType"/> is "apiKey". <c>config.yaml</c> is
        /// gitignored - never commit real values here.
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }
    }
}
