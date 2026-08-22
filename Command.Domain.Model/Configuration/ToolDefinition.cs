namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures a single tool an agent can call. Every tool an agent uses - whether a remote
    /// Model Context Protocol tool, the local shell tool, or the custom web-search tool - is a
    /// single entry in <see cref="AgentDefinition.Tools"/>, distinguished by <see cref="Type"/>.
    /// </summary>
    public sealed class ToolDefinition
    {
        /// <summary>One of "mcp", "shell", or "web-search".</summary>
        public required string Type { get; set; }

        /// <summary>
        /// A unique (per-agent) identifier for this tool. For "mcp" tools this must match the
        /// remote tool name exposed by the MCP server; for "shell"/"web-search" tools it is just
        /// a label used for logging and for matching entries across config reloads (e.g. when
        /// merging back a real secret value over a redacted placeholder).
        /// </summary>
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

        // --- "shell" tool fields ---------------------------------------------------------------
        // Configures a local Microsoft.Agents.AI.Tools.Shell.LocalShellExecutor tool, attached to
        // the agent alongside its own ShellEnvironmentProvider context. See
        // https://learn.microsoft.com/agent-framework/integrations/by-component/tools/shell-tools.

        /// <summary>
        /// "stateless" (default - a fresh shell process per call) or "persistent" (one shell
        /// reused across calls within the same agent instance; state such as the working
        /// directory or environment variables carries between calls). Only used when
        /// <see cref="Type"/> is "shell".
        /// </summary>
        public string Mode { get; set; } = "stateless";

        /// <summary>
        /// Must be explicitly set true to acknowledge that shell execution can modify files,
        /// launch processes, access credentials, and reach external systems - mirrors
        /// <c>LocalShellExecutorOptions.AcknowledgeUnsafe</c>. The tool is not created unless this
        /// is true. Only used when <see cref="Type"/> is "shell".
        /// </summary>
        public bool AcknowledgeUnsafe { get; set; }

        /// <summary>
        /// Whether each shell command invocation requires human/caller approval before it runs
        /// (maps to <c>AsAIFunction(requireApproval:)</c>). Defaults to true (safest option) - set
        /// false only for trusted, fully-automated scenarios. Only used when <see cref="Type"/> is
        /// "shell".
        /// </summary>
        public bool RequireApproval { get; set; } = true;

        // --- "web-search" tool fields ------------------------------------------------------------
        // Configures a custom web-search tool backed by a real third-party search provider's REST
        // API. Independent of any model-provider-hosted web search the harness may attach
        // automatically - see HarnessOptionsDefinition.DisableWebSearch.

        /// <summary>
        /// Which search-provider REST API to call: "tavily" (default), "bing", "google", or
        /// "serpapi". Case-insensitive. Only used when <see cref="Type"/> is "web-search".
        /// </summary>
        public string Provider { get; set; } = "tavily";

        /// <summary>
        /// The chosen search provider's API key, stored directly in <c>config.yaml</c>. Only used
        /// when <see cref="Type"/> is "web-search".
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Maximum number of search results to return to the model. Defaults to 5. Only used when
        /// <see cref="Type"/> is "web-search".
        /// </summary>
        public int MaxResults { get; set; } = 5;

        /// <summary>
        /// Tavily-only: search thoroughness/latency trade-off - "basic" (default), "advanced",
        /// "fast", or "ultra-fast". Ignored by other providers. Only used when <see cref="Type"/>
        /// is "web-search".
        /// </summary>
        public string SearchDepth { get; set; } = "basic";

        /// <summary>
        /// Google Custom Search-only: the Programmable Search Engine ID ("cx" parameter).
        /// Required when <see cref="Provider"/> is "google"; ignored by other providers. Only used
        /// when <see cref="Type"/> is "web-search".
        /// </summary>
        public string? SearchEngineId { get; set; }

        /// <summary>
        /// SerpApi-only: which underlying search engine SerpApi should proxy to (for example
        /// "google", "bing"). Defaults to "google". Ignored by other providers. Only used when
        /// <see cref="Type"/> is "web-search".
        /// </summary>
        public string SearchEngine { get; set; } = "google";
    }
}
