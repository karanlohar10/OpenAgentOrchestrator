using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Service.Command.Controllers
{
    /// <summary>
    /// Configuration is defined entirely in <c>config.yaml</c> (hand-edited on disk, gitignored -
    /// see config.sample.yaml for the template) rather than via CRUD endpoints. Alongside the
    /// on-disk $reload action, this controller also exposes a single full-replace <c>PUT</c> (and
    /// a no-write <c>$validate</c> dry-run) so tooling such as the OpenAgentOrchestratorAdmin
    /// visual builder can write the whole config back in one shot - see
    /// <see cref="IConfigStore.SaveAsync"/>/<see cref="IConfigStore.ValidateAsync"/> for the
    /// secret-sentinel merge + validation pipeline behind both.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("command/api/v{version:apiVersion}/config")]
    public sealed class ConfigCommandController : ControllerBase
    {
        private readonly IConfigStore _configStore;

        public ConfigCommandController(IConfigStore configStore)
        {
            _configStore = configStore;
        }

        /// <summary>
        /// Re-reads and re-validates <c>config.yaml</c> from disk, replacing the in-memory
        /// snapshot used by all query/execute endpoints. If validation fails, the previously
        /// loaded configuration remains active and the validation errors are returned.
        /// </summary>
        [HttpPost("$reload")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Reload(CancellationToken cancellationToken)
        {
            var validation = await _configStore.ReloadAsync(cancellationToken);
            if (!validation.IsValid)
                return BadRequest(new { errors = validation.Errors, warnings = validation.Warnings });

            return Ok(new { message = "config.yaml reloaded.", warnings = validation.Warnings });
        }

        /// <summary>
        /// Full-replace save: merges secrets (blank/redacted-placeholder fields keep their real
        /// existing values), validates, and - only if valid - writes <c>config.yaml</c> and
        /// swaps in the new in-memory snapshot. Nothing is written on validation failure.
        /// </summary>
        [HttpPut]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Save([FromBody] PlatformConfig candidate, CancellationToken cancellationToken)
        {
            var validation = await _configStore.SaveAsync(candidate, cancellationToken);
            if (!validation.IsValid)
                return BadRequest(new { errors = validation.Errors, warnings = validation.Warnings });

            return Ok(new { message = "config.yaml saved.", warnings = validation.Warnings });
        }

        /// <summary>
        /// Dry-run of the same merge+validate pipeline as <c>PUT</c>, without writing to disk or
        /// changing the active in-memory config - backs a "Validate" action that doesn't require
        /// saving first.
        /// </summary>
        [HttpPost("$validate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Validate([FromBody] PlatformConfig candidate, CancellationToken cancellationToken)
        {
            var validation = await _configStore.ValidateAsync(candidate, cancellationToken);
            if (!validation.IsValid)
                return BadRequest(new { errors = validation.Errors, warnings = validation.Warnings });

            return Ok(new { message = "config is valid.", warnings = validation.Warnings });
        }
    }
}
