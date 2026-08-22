namespace OpenAgentOrchestrator.Query.Domain.Model
{
    /// <summary>
    /// Durable, per-session record of every workflow step's output, persisted as a single JSON
    /// file. This is the read-side projection returned by the checkpoints query endpoint - it is
    /// also the shape written by Command.Application's checkpoint store, so the two must stay in
    /// sync.
    /// </summary>
    /// <remarks>
    /// To redo an earlier step (rewind), pass that step's <see cref="StepCheckpointRecordResponse.StepIndex"/>
    /// as <c>ResumeRequest.StepIndex</c> on <c>$resume</c> - "redo step N" with nothing further to
    /// resolve on the caller's side. The underlying MAF-native checkpoint id that once made this
    /// possible is an internal implementation detail of the resume mechanism and is deliberately
    /// not exposed here.
    /// </remarks>
    public sealed class SessionCheckpointsResponse
    {
        public required string SessionId { get; set; }
        public required string OrchestratorId { get; set; }
        public required string Pattern { get; set; }
        public required string Input { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>running | pending_approval | completed | failed | rejected</summary>
        public string Status { get; set; } = "running";

        public string? FinalOutput { get; set; }
        public string? Error { get; set; }
        public List<StepCheckpointRecordResponse> Steps { get; set; } = [];
        public string? PendingAgentName { get; set; }
        public int? PendingStepIndex { get; set; }
        public string? PendingOutput { get; set; }
        public string? PendingApprovalPrompt { get; set; }

        /// <summary>
        /// <see langword="true"/> when the pending step's output was parsed as a clarification
        /// envelope with <c>needsClarification: true</c> - see
        /// <see cref="SessionStatusResponse.PendingNeedsClarification"/>.
        /// </summary>
        public bool PendingNeedsClarification { get; set; }

        /// <summary>The clarifying question the agent asked, when <see cref="PendingNeedsClarification"/> is true.</summary>
        public string? PendingClarificationQuestion { get; set; }
    }

    /// <summary>A single durable step checkpoint - one per completed agent step.</summary>
    public sealed class StepCheckpointRecordResponse
    {
        public int StepIndex { get; set; }
        public required string AgentName { get; set; }
        public required string Status { get; set; }
        public string? Output { get; set; }
        public double DurationMs { get; set; }
        public DateTime RecordedAt { get; set; }

        /// <summary>True if this step's output was substituted via a human-in-the-loop edit.</summary>
        public bool Edited { get; set; }
    }
}
