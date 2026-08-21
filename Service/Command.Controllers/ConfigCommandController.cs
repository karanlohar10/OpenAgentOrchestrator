using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Configuration;

namespace OpenAgentOrchestrator.Service.Command.Controllers
{
    /// <summary>
    /// Configuration is defined entirely in <c>config.yaml</c> (hand-edited on disk, gitignored -
    /// see config.sample.yaml for the template) rather than via CRUD endpoints. This controller
    /// only exposes a way to make an on-disk edit take effect without restarting the service.
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
    }
}
