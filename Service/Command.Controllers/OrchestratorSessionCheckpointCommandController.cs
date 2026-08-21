using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;

namespace OpenAgentOrchestrator.Service.Command.Controllers
{
    /// <summary>
    /// Cleanup endpoint for a session's durable checkpoint, for any orchestrator defined in
    /// <c>config.yaml</c>. Only needs <c>sessionId</c> - it resolves the orchestrator itself from
    /// the session's durable <c>WorkflowCheckpoint</c> row (see
    /// <see cref="IWorkflowCheckpointStore"/>), since a session only ever has a checkpoint to
    /// delete if checkpointing was enabled for it in the first place.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("command/api/v{version:apiVersion}/sessions/{sessionId}/checkpoint")]
    public sealed class OrchestratorSessionCheckpointCommandController : ControllerBase
    {
        private readonly IConfigStore _configStore;
        private readonly IWorkflowEngine _engine;
        private readonly IWorkflowCheckpointStore _checkpointStore;

        public OrchestratorSessionCheckpointCommandController(IConfigStore configStore, IWorkflowEngine engine, IWorkflowCheckpointStore checkpointStore)
        {
            _configStore = configStore;
            _engine = engine;
            _checkpointStore = checkpointStore;
        }

        /// <summary>
        /// Deletes the durable checkpoint persisted for a session, once the session has reached a
        /// terminal state (<c>completed</c>/<c>failed</c>/<c>rejected</c>). Returns
        /// <c>409 Conflict</c> if the session is still <c>running</c>/<c>pending_approval</c>, so
        /// an active or resumable session's durable state can't be deleted out from under it.
        /// </summary>
        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(string sessionId, CancellationToken cancellationToken)
        {
            var checkpoint = await _checkpointStore.LoadAsync(sessionId, cancellationToken);
            if (checkpoint is null)
                return NotFound(new { error = $"No checkpoint found for session '{sessionId}'." });

            var orchestrator = _configStore.GetOrchestrator(checkpoint.OrchestratorId);
            if (orchestrator is null)
                return NotFound(new { error = $"Orchestrator '{checkpoint.OrchestratorId}' is not configured." });

            try
            {
                var deleted = await _engine.DeleteCheckpointAsync(orchestrator, sessionId, cancellationToken);
                if (!deleted)
                    return NotFound(new { error = $"No checkpoint found for session '{sessionId}'." });

                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
        }
    }
}
