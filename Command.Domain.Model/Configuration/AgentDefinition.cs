namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures a single agent participating in a sequential workflow. Handoff-specific
    /// properties (handoff targets) are intentionally not present here - only the sequential
    /// pattern is supported by this service.
    /// </summary>
    public sealed class AgentDefinition
    {
        public required string Name { get; set; }
        public string? Instructions { get; set; }

        /// <summary>
        /// Path (relative to <c>ConfigYaml.InstructionsRoot</c> - see <see cref="ConfigYamlOptions"/>)
        /// to a text file whose contents are used as <see cref="Instructions"/> when
        /// <see cref="Instructions"/> isn't set inline. Resolved once at config-load time by
        /// <c>ConfigStore</c>; lets long/complex prompts live as separate files instead of
        /// inline YAML strings. Ignored (with a warning) if <see cref="Instructions"/> is also set.
        /// </summary>
        public string? InstructionsFile { get; set; }

        /// <summary>
        /// Provider id to use for this agent. Optional - when omitted, falls back to
        /// <c>AgentDefaults.DefaultProvider</c> (bound from appsettings.json) at agent-creation
        /// time. Falling back is only possible when a default is configured.
        /// </summary>
        public string? Provider { get; set; }

        /// <summary>
        /// Model name to use for this agent. Optional - when omitted, falls back to
        /// <c>AgentDefaults.DefaultModel</c> (bound from appsettings.json) at agent-creation
        /// time. Falling back is only possible when a default is configured.
        /// </summary>
        public string? Model { get; set; }
        public List<ToolDefinition>? Tools { get; set; }

        /// <summary>
        /// Configures Microsoft Agent Framework structured-output enforcement
        /// (<c>ChatOptions.ResponseFormat</c>) for this agent. Optional - when omitted, the agent
        /// behaves exactly as before (plain text output, enforced only by prompt instructions).
        /// </summary>
        public ResponseFormatDefinition? ResponseFormat { get; set; }

        /// <summary>
        /// Selects the agent runtime: "chat" (default, a plain <c>ChatClientAgent</c>) or
        /// "harness" (Microsoft Agent Framework's opinionated Agent Harness runtime - see
        /// <see cref="Harness"/> for its options). See
        /// https://learn.microsoft.com/agent-framework/concepts/harness.
        /// </summary>
        public string AgentType { get; set; } = "chat";

        /// <summary>
        /// Agent Harness configuration. Only used when <see cref="AgentType"/> is "harness";
        /// optional even then (harness agents work with all-default options).
        /// </summary>
        public HarnessOptionsDefinition? Harness { get; set; }

        /// <summary>
        /// Configures an optional local shell execution tool for this agent (either a plain chat
        /// agent or a harness agent). Disabled unless <see cref="ShellToolDefinition.Enabled"/> is
        /// true. See https://learn.microsoft.com/agent-framework/integrations/by-component/tools/shell-tools.
        /// </summary>
        public ShellToolDefinition? ShellTool { get; set; }

        /// <summary>
        /// Configures an optional custom web-search tool for this agent (either a plain chat
        /// agent or a harness agent), backed by a real search-provider REST API. Disabled unless
        /// <see cref="WebSearchToolDefinition.Enabled"/> is true. Independent of
        /// <see cref="HarnessOptionsDefinition.DisableWebSearch"/> - see that property's docs for
        /// how the two interact.
        /// </summary>
        public WebSearchToolDefinition? WebSearchTool { get; set; }
    }

    /// <summary>
    /// Configures Microsoft Agent Framework's <c>HarnessAgentOptions</c> (see
    /// <c>Microsoft.Agents.AI.Harness.AsHarnessAgent</c>). All properties are optional - the
    /// harness runtime supplies sensible defaults for anything left unset.
    /// </summary>
    public sealed class HarnessOptionsDefinition
    {
        /// <summary>
        /// Extra system-level instructions steering how the harness itself drives the agent
        /// (tool-use discipline, planning behavior, etc.) - distinct from the agent's own
        /// <see cref="AgentDefinition.Instructions"/>, which become <c>ChatOptions.Instructions</c>.
        /// </summary>
        public string? HarnessInstructions { get; set; }

        /// <summary>Maximum number of tokens the harness will keep in the active context window.</summary>
        public int? MaxContextWindowTokens { get; set; }

        /// <summary>Maximum number of tokens the harness will request per model response.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Disables the harness's automatically-attached, model-provider-hosted web search tool
        /// (maps to <c>HarnessAgentOptions.DisableWebSearch</c>). Defaults to <see langword="false"/>
        /// (hosted web search enabled), matching the framework's own default.
        /// </summary>
        /// <remarks>
        /// Some <c>IChatClient</c> providers/deployments reject the hosted tool's provider-specific
        /// request parameters (for example, an Azure OpenAI deployment returning
        /// <c>HTTP 400 unknown_parameter: web_search_options</c>). Set this to <see langword="true"/>
        /// when the configured provider doesn't support the hosted tool, or when you're attaching
        /// your own search tool via <see cref="AgentDefinition.WebSearchTool"/> instead - per
        /// Microsoft's guidance, leaving this <see langword="false"/> while also adding a custom
        /// web-search tool gives the agent two search tools at once, which is redundant and can
        /// confuse the model. See
        /// https://learn.microsoft.com/agent-framework/concepts/harness.
        /// </remarks>
        public bool DisableWebSearch { get; set; }
    }

    /// <summary>
    /// Configures a <c>Microsoft.Agents.AI.Tools.Shell.LocalShellExecutor</c> local shell tool,
    /// attached to the agent alongside its own <c>ShellEnvironmentProvider</c> context.
    /// </summary>
    public sealed class ShellToolDefinition
    {
        /// <summary>Whether the shell tool is attached to this agent at all. Defaults to false.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// "stateless" (default - a fresh shell process per call) or "persistent" (one shell
        /// reused across calls within the same agent instance; state such as the working
        /// directory or environment variables carries between calls).
        /// </summary>
        public string Mode { get; set; } = "stateless";

        /// <summary>
        /// Must be explicitly set true to acknowledge that shell execution can modify files,
        /// launch processes, access credentials, and reach external systems - mirrors
        /// <c>LocalShellExecutorOptions.AcknowledgeUnsafe</c>. The tool is not created unless
        /// this is true, regardless of <see cref="Enabled"/>.
        /// </summary>
        public bool AcknowledgeUnsafe { get; set; }

        /// <summary>
        /// Whether each shell command invocation requires human/caller approval before it runs
        /// (maps to <c>AsAIFunction(requireApproval:)</c>). Defaults to true (safest option) -
        /// set false only for trusted, fully-automated scenarios.
        /// </summary>
        public bool RequireApproval { get; set; } = true;
    }

    /// <summary>
    /// Configures a custom web-search tool for an agent, backed by a real third-party search
    /// provider's REST API (chosen via <see cref="Provider"/>). Independent of any
    /// model-provider-hosted web search the harness may attach automatically - see
    /// <see cref="HarnessOptionsDefinition.DisableWebSearch"/>.
    /// </summary>
    public sealed class WebSearchToolDefinition
    {
        /// <summary>Whether the web-search tool is attached to this agent at all. Defaults to false.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Which search-provider REST API to call: "tavily" (default), "bing", "google", or
        /// "serpapi". Case-insensitive. An unrecognized value fails config validation with a
        /// clear error at startup.
        /// </summary>
        public string Provider { get; set; } = "tavily";

        /// <summary>
        /// The chosen provider's API key, stored directly in config.yaml (consistent with this
        /// project's "secrets live in config.yaml" convention for tools/providers/agents).
        /// </summary>
        public required string ApiKey { get; set; }

        /// <summary>Maximum number of search results to return to the model. Defaults to 5.</summary>
        public int MaxResults { get; set; } = 5;

        /// <summary>
        /// Tavily-only: search thoroughness/latency trade-off - "basic" (default), "advanced",
        /// "fast", or "ultra-fast". Ignored by other providers.
        /// </summary>
        public string SearchDepth { get; set; } = "basic";

        /// <summary>
        /// Google Custom Search-only: the Programmable Search Engine ID ("cx" parameter).
        /// Required when <see cref="Provider"/> is "google"; ignored by other providers.
        /// </summary>
        public string? SearchEngineId { get; set; }

        /// <summary>
        /// SerpApi-only: which underlying search engine SerpApi should proxy to (for example
        /// "google", "bing"). Defaults to "google". Ignored by other providers.
        /// </summary>
        public string SearchEngine { get; set; } = "google";
    }
}
