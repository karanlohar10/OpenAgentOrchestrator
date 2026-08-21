namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    /// <summary>
    /// Platform-wide fallback provider/model, bound from the <c>AgentDefaults</c> configuration
    /// section. Used by <see cref="OpenAgentOrchestrator.Command.Application.Agents.AgentFactory"/>
    /// (and validated by <see cref="IConfigValidator"/>) whenever an agent definition omits its
    /// own Provider/Model.
    /// </summary>
    public sealed class AgentDefaults
    {
        /// <summary>Provider id used when an agent does not specify one. Optional.</summary>
        public string? DefaultProvider { get; set; }

        /// <summary>Model name used when an agent does not specify one. Optional.</summary>
        public string? DefaultModel { get; set; }
    }
}
