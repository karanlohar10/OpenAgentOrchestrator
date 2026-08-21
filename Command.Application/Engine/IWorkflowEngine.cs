using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    public interface IWorkflowEngine
    {
        Task<ExecuteResponse> ExecuteAsync(OrchestratorDefinition orchestrator, ExecuteRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the durable step checkpoints persisted for a session (requires checkpointing to
        /// be enabled for the orchestrator). Returns null if checkpointing is disabled or no
        /// checkpoint document exists for the session.
        /// </summary>
        Task<SessionCheckpointDocument?> GetCheckpointsAsync(OrchestratorDefinition orchestrator, string sessionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resumes a session's workflow from a durable checkpoint identified by
        /// <see cref="ResumeRequest.CheckpointId"/>: passing the session's own current checkpoint
        /// answers an outstanding human-in-the-loop review or continues execution after a
        /// crash/failure (today's behavior); passing an earlier, already-completed step's
        /// checkpoint rewinds the session back to that step and re-executes it and every step
        /// after it, discarding their prior outputs - usable from any session status, in-flight or
        /// terminal. Requires checkpointing to be enabled for the orchestrator; there is no
        /// non-checkpointed ("live", in-memory-only) resume path.
        /// </summary>
        Task<ExecuteResponse> ResumeAsync(OrchestratorDefinition orchestrator, string sessionId, ResumeRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the durable checkpoint for a session once it has reached a terminal state.
        /// Returns <see langword="false"/> if checkpointing is disabled or no checkpoint exists
        /// for the session; throws <see cref="InvalidOperationException"/> if the session is
        /// still running or pending approval.
        /// </summary>
        Task<bool> DeleteCheckpointAsync(OrchestratorDefinition orchestrator, string sessionId, CancellationToken cancellationToken = default);
    }
}
