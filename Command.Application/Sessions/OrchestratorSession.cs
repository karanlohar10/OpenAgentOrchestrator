using OpenAgentOrchestrator.Command.Contract;

namespace OpenAgentOrchestrator.Command.Application.Sessions
{
    public sealed class OrchestratorSession
    {
        public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
        public required string OrchestratorId { get; init; }

        /// <summary>running | pending_approval | completed | failed | rejected</summary>
        public string Status { get; set; } = "running";

        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public string? PendingApprovalPrompt { get; set; }
        public List<AgentStepResult> Steps { get; } = [];

        /// <summary>
        /// The id of the MAF RequestPort of an outstanding human-in-the-loop request, present
        /// while <see cref="Status"/> is <c>pending_approval</c>. Used to answer it - either
        /// against the still-open live run (see <see cref="Engine.ILiveWorkflowRunStore"/>, for
        /// orchestrators without checkpointing) or against a rehydrated MAF checkpoint (for
        /// orchestrators with checkpointing enabled).
        /// </summary>
        public string? PendingRequestPortId { get; set; }

        /// <summary>The 0-based step index awaiting human review, when <see cref="PendingRequestPortId"/> is set.</summary>
        public int? PendingStepIndex { get; set; }

        /// <summary>The name of the agent whose output is awaiting human review.</summary>
        public string? PendingAgentName { get; set; }

        /// <summary>The agent's original (unedited) output text awaiting human review/approval.</summary>
        public string? PendingOutput { get; set; }
    }
}
