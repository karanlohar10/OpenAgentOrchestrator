namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures a single orchestrator workflow. Only the "sequential" pattern is supported by
    /// this service - concurrent/handoff/single (and their aggregator/handoff-target concepts)
    /// were intentionally not migrated.
    /// </summary>
    public sealed class OrchestratorDefinition
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }

        /// <summary>Always "sequential" for this service - see <see cref="Configuration.ConfigValidator"/>.</summary>
        public required string Pattern { get; set; }

        public required List<AgentDefinition> Agents { get; set; }

        /// <summary>
        /// Per-orchestrator checkpointing configuration — every orchestrator fully owns its own
        /// config directly (no platform-level default to inherit from).
        /// </summary>
        public required CheckpointingDefinition Checkpointing { get; set; }
    }
}
