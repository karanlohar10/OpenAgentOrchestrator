using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Query.Application.Services;
using OpenAgentOrchestrator.Query.Domain.Model;

namespace OpenAgentOrchestrator.Service.Query.Controllers
{
    /// <summary>
    /// Read-only endpoints for orchestrators and their sessions. The orchestrator summary is
    /// identified by the <c>orchestratorId</c> route segment; the session-scoped endpoints
    /// (session status, checkpoints) are identified by <c>sessionId</c> alone, since a session's
    /// orchestrator is already durably recorded on its checkpoint.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("query/api/v{version:apiVersion}/orchestrators/{orchestratorId}")]
    public sealed class OrchestratorSessionsQueryController : ControllerBase
    {
        private readonly IOrchestratorQueryService _queryService;

        public OrchestratorSessionsQueryController(IOrchestratorQueryService queryService)
        {
            _queryService = queryService;
        }

        /// <summary>Gets the orchestrator's definition summary.</summary>
        [HttpGet]
        [ProducesResponseType<OrchestratorSummaryResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<OrchestratorSummaryResponse> Get(string orchestratorId)
        {
            var summary = _queryService.GetOrchestratorSummary(orchestratorId);
            if (summary is null)
                return NotFound(new { error = $"Orchestrator '{orchestratorId}' is not configured." });

            return Ok(summary);
        }

        /// <summary>Gets a session's current status.</summary>
        [HttpGet("/query/api/v{version:apiVersion}/sessions/{sessionId}")]
        [ProducesResponseType<SessionStatusResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<SessionStatusResponse> GetSessionStatus(string sessionId)
        {
            var status = _queryService.GetSessionStatus(sessionId);
            if (status is null)
                return NotFound(new { error = $"Session '{sessionId}' not found." });

            return Ok(status);
        }

        /// <summary>Gets the durable step checkpoints persisted for a session (requires checkpointing to be enabled for the orchestrator).</summary>
        [HttpGet("/query/api/v{version:apiVersion}/sessions/{sessionId}/checkpoints")]
        [ProducesResponseType<SessionCheckpointsResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<SessionCheckpointsResponse>> GetCheckpoints(string sessionId, CancellationToken cancellationToken)
        {
            var document = await _queryService.GetCheckpointsAsync(sessionId, cancellationToken);
            if (document is null)
                return NotFound(new { error = $"No checkpoints found for session '{sessionId}'." });

            return Ok(document);
        }
    }
}
