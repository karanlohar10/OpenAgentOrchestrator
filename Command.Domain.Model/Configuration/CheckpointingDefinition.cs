namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures durable step-level checkpointing (and MAF-native graph checkpointing) for an
    /// orchestrator run. Can be set globally (<see cref="PlatformConfig.Checkpointing"/>) and/or
    /// overridden per-orchestrator (<see cref="OrchestratorDefinition.Checkpointing"/>) - the
    /// per-orchestrator value wins when present.
    /// </summary>
    public sealed class CheckpointingDefinition
    {
        /// <summary>Whether checkpointing is enabled for the orchestrator run.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Human-in-the-loop review-gate settings, nested here (rather than as a sibling
        /// top-level block on <see cref="OrchestratorDefinition"/>) because pausing a run for
        /// approval has no durable resume mechanism other than this checkpointing: this service
        /// no longer supports resuming a non-checkpointed ("live", in-memory-only) paused run, so
        /// <see cref="HumanInLoopDefinition.Enabled"/> being true only makes sense when
        /// <see cref="Enabled"/> is also true (enforced by <c>ConfigValidator</c>).
        /// </summary>
        public HumanInLoopDefinition? HumanInLoop { get; set; }
    }

    /// <summary>Configures the human-in-the-loop review gate inserted after every agent step.</summary>
    public sealed class HumanInLoopDefinition
    {
        public bool Enabled { get; set; }
        public string? ApprovalPrompt { get; set; }
    }
}
