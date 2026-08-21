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
    }
}
