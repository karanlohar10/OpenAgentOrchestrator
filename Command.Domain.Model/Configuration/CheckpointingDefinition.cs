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

        /// <summary>
        /// Opt-in (default <see langword="false"/>). When <see langword="true"/>, every agent in
        /// the orchestrator is instructed to respond with a fixed JSON envelope -
        /// <c>{ "needsClarification": bool, "clarificationQuestion": string?, "content": string }</c>
        /// - and the review gate parses it so the pending-review payload can tell callers whether
        /// the paused step is a genuine question the agent needs answered (in which case the
        /// human's answer is routed back to the *same* agent for another turn) or a routine step
        /// awaiting approval/edit (in which case the answer flows to the next agent, as normal).
        /// A step still pauses for review either way - this only changes where the answer goes
        /// afterwards, and enriches the pending-review payload with
        /// <c>PendingNeedsClarification</c>/<c>PendingClarificationQuestion</c>. Only meaningful
        /// when <see cref="Enabled"/> is also <see langword="true"/> (enforced by
        /// <c>ConfigValidator</c>).
        /// </summary>
        public bool EnableClarificationFlag { get; set; }
    }
}
