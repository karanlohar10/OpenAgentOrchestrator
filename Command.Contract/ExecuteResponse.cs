namespace OpenAgentOrchestrator.Command.Contract
{
    public sealed class ExecuteResponse
    {
        public required string SessionId { get; set; }

        /// <summary>completed | pending_approval | failed | rejected</summary>
        public required string Status { get; set; }

        public string? Output { get; set; }
        public List<AgentStepResult>? Steps { get; set; }
        public string? Error { get; set; }

        /// <summary>
        /// <see langword="true"/> when a <c>pending_approval</c> response's pending step output was
        /// parsed as a clarification envelope with <c>needsClarification: true</c> - i.e. the agent
        /// is asking a genuine question rather than presenting a routine result awaiting approval.
        /// Always <see langword="false"/> when the orchestrator's
        /// <c>checkpointing.humanInLoop.enableClarificationFlag</c> is off. The answer is still
        /// submitted via the same <c>$resume</c> endpoint either way.
        /// </summary>
        public bool PendingNeedsClarification { get; set; }

        /// <summary>The clarifying question the agent asked, when <see cref="PendingNeedsClarification"/> is true.</summary>
        public string? PendingClarificationQuestion { get; set; }
    }

    public sealed class AgentStepResult
    {
        public required string AgentName { get; set; }
        public required string Status { get; set; }
        public string? Output { get; set; }
        public double DurationMs { get; set; }
    }
}
