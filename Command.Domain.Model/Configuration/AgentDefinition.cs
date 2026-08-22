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
        /// Opt-in configuration for the Agent Framework's todo/agent-mode planning primitives and
        /// bounded todo-completion loop (see <see cref="PlanningDefinition"/>). Optional - omitted
        /// entirely by default, leaving agent behavior unchanged. Works with both "chat" and
        /// "harness" <see cref="AgentType"/>.
        /// </summary>
        public PlanningDefinition? Planning { get; set; }
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
        /// your own search tool via a <c>tools:</c> entry with <c>type: web-search</c> instead -
        /// per Microsoft's guidance, leaving this <see langword="false"/> while also adding a
        /// custom web-search tool gives the agent two search tools at once, which is redundant and
        /// can confuse the model. See
        /// https://learn.microsoft.com/agent-framework/concepts/harness.
        /// </remarks>
        public bool DisableWebSearch { get; set; }
    }
}
