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
}
