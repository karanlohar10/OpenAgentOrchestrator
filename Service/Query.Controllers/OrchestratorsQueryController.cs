using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Service.Query.Controllers
{
    /// <summary>
    /// Read-only endpoints for orchestrator configuration (definition + nested agents + tools),
    /// projected from the in-memory <c>config.yaml</c> snapshot.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("query/api/v{version:apiVersion}/orchestrators-config")]
    public sealed class OrchestratorsQueryController : ControllerBase
    {
        private readonly IConfigStore _configStore;

        public OrchestratorsQueryController(IConfigStore configStore)
        {
            _configStore = configStore;
        }

        /// <summary>Lists all orchestrator definitions including their nested agents and tools (secrets redacted).</summary>
        [HttpGet]
        [ProducesResponseType<List<OrchestratorDefinition>>(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<OrchestratorDefinition>>> GetAll(CancellationToken cancellationToken)
        {
            var config = await _configStore.GetConfigAsync(cancellationToken);
            return Ok(config.Orchestrators.Select(ConfigRedaction.Redact).ToList());
        }

        /// <summary>Gets a single orchestrator definition (with nested agents and tools, secrets redacted) by its id.</summary>
        [HttpGet("{orchestratorId}")]
        [ProducesResponseType<OrchestratorDefinition>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<OrchestratorDefinition>> GetById(string orchestratorId, CancellationToken cancellationToken)
        {
            var orchestrator = await _configStore.GetOrchestratorAsync(orchestratorId, cancellationToken);
            if (orchestrator is null)
                return NotFound(new { error = $"Orchestrator '{orchestratorId}' not found." });

            return Ok(ConfigRedaction.Redact(orchestrator));
        }
    }
}
