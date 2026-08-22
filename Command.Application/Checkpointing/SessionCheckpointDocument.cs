namespace OpenAgentOrchestrator.Command.Application.Checkpointing
{
    /// <summary>
    /// Durable, per-session record of every workflow step's output, persisted as a single JSON
    /// file (see <see cref="IWorkflowCheckpointStore"/>). This is the source of truth for listing
    /// checkpoints and for the collapsed "resume" (answer the pending human-in-the-loop request)
    /// operation.
    /// </summary>
    public sealed class SessionCheckpointDocument
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
        public List<StepCheckpointRecord> Steps { get; set; } = [];

        /// <summary>
        /// The MAF-native graph checkpoint id for this run's latest superstep, present only when
        /// MAF structural checkpointing (<c>CheckpointManager</c> +
        /// <c>FileSystemJsonCheckpointStore</c>) is wired for the run. Paired with
        /// <see cref="SessionId"/> (MAF's own checkpoint session id is always identical to ours,
        /// since we supply it directly to MAF's run APIs) to reconstruct a MAF
        /// <c>CheckpointInfo</c> on resume.
        /// </summary>
        public string? CheckpointId { get; set; }

        /// <summary>
        /// The id of the MAF RequestPort of an outstanding human-in-the-loop request, present
        /// only while <see cref="Status"/> is <c>pending_approval</c>.
        /// </summary>
        public string? PendingRequestPortId { get; set; }

        /// <summary>The 0-based step index (into <see cref="Steps"/>) awaiting human review, when <see cref="PendingRequestPortId"/> is set.</summary>
        public int? PendingStepIndex { get; set; }

        /// <summary>The name of the agent whose output is awaiting human review.</summary>
        public string? PendingAgentName { get; set; }

        /// <summary>The agent's original (unedited) output text awaiting human review/approval.</summary>
        public string? PendingOutput { get; set; }

        /// <summary>The orchestrator's configured human-in-the-loop approval prompt, to surface alongside the pending output.</summary>
        public string? PendingApprovalPrompt { get; set; }

        /// <summary>
        /// <see langword="true"/> when the pending step's output was parsed as a clarification
        /// envelope with <c>needsClarification: true</c> - see
        /// <see cref="Application.Sessions.OrchestratorSession.PendingNeedsClarification"/>.
        /// </summary>
        public bool PendingNeedsClarification { get; set; }

        /// <summary>The clarifying question the agent asked, when <see cref="PendingNeedsClarification"/> is true.</summary>
        public string? PendingClarificationQuestion { get; set; }

        /// <summary>
        /// Store-specific optimistic-concurrency stamp, populated by
        /// <see cref="IWorkflowCheckpointStore.LoadAsync"/> implementations that support
        /// concurrency detection (e.g. the Postgres-backed store) and consumed by their
        /// <see cref="IWorkflowCheckpointStore.SaveAsync"/> to detect a conflicting concurrent
        /// write. Ignored by implementations that don't support it (e.g. the file-based store);
        /// callers that never loaded a document (a brand-new session) leave this
        /// <see langword="null"/>, which such stores treat as "no concurrency check requested".
        /// </summary>
        public DateTime? ConcurrencyStamp { get; set; }
    }

    /// <summary>A single durable step checkpoint - one per completed agent step.</summary>
    public sealed class StepCheckpointRecord
    {
        public int StepIndex { get; set; }
        public required string AgentName { get; set; }
        public required string Status { get; set; }
        public string? Output { get; set; }
        public double DurationMs { get; set; }
        public DateTime RecordedAt { get; set; }

        /// <summary>True if this step's output was substituted via a human-in-the-loop edit.</summary>
        public bool Edited { get; set; }

        /// <summary>
        /// The MAF-native graph checkpoint id capturing the workflow's state immediately after
        /// this step completed - i.e. the same value written onto
        /// <see cref="SessionCheckpointDocument.CheckpointId"/> at the moment this step was
        /// persisted (see <c>WorkflowEngine.PersistCompletedStepAsync</c>). Used to target a
        /// rewind back to this step via <see cref="OpenAgentOrchestrator.Command.Contract.ResumeRequest.CheckpointId"/>.
        /// Null for steps recorded before this field was introduced.
        /// </summary>
        public string? CheckpointId { get; set; }
    }
}
