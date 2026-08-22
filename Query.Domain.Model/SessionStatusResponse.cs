namespace OpenAgentOrchestrator.Query.Domain.Model
{
    public sealed class SessionStatusResponse
    {
        public required string SessionId { get; set; }
        public required string OrchestratorId { get; set; }

        /// <summary>running | pending_approval | completed | failed | rejected</summary>
        public required string Status { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? PendingApprovalPrompt { get; set; }

        /// <summary>The 0-based step index awaiting human review, when <see cref="Status"/> is <c>pending_approval</c>.</summary>
        public int? PendingStepIndex { get; set; }

        /// <summary>The name of the agent whose output is awaiting human review.</summary>
        public string? PendingAgentName { get; set; }

        /// <summary>The agent's output text awaiting human review/edit.</summary>
        public string? PendingOutput { get; set; }

        /// <summary>
        /// <see langword="true"/> when the pending step's output was parsed as a clarification
        /// envelope with <c>needsClarification: true</c> - i.e. the agent is asking a genuine
        /// question rather than presenting a routine result awaiting approval. Always
        /// <see langword="false"/> when the orchestrator's
        /// <c>checkpointing.humanInLoop.enableClarificationFlag</c> is off. Answer it the same way
        /// as any other pending review, via <c>$resume</c>.
        /// </summary>
        public bool PendingNeedsClarification { get; set; }

        /// <summary>The clarifying question the agent asked, when <see cref="PendingNeedsClarification"/> is true.</summary>
        public string? PendingClarificationQuestion { get; set; }
    }
}
