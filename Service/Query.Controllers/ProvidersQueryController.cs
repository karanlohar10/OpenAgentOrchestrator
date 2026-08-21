using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Service.Query.Controllers
{
    /// <summary>
    /// Read-only endpoints for AI provider definitions, projected from the in-memory
    /// <c>config.yaml</c> snapshot. <see cref="ProviderDefinition.ApiKey"/> is always redacted in
    /// the response - config.yaml stores it literally, so it must never be echoed back over HTTP.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("query/api/v{version:apiVersion}/providers")]
    public sealed class ProvidersQueryController : ControllerBase
    {
        private readonly IConfigStore _configStore;

        public ProvidersQueryController(IConfigStore configStore)
        {
            _configStore = configStore;
        }

        /// <summary>Lists all configured AI providers (API keys redacted).</summary>
        [HttpGet]
        [ProducesResponseType<List<ProviderDefinition>>(StatusCodes.Status200OK)]
        public async Task<ActionResult<List<ProviderDefinition>>> GetAll(CancellationToken cancellationToken)
        {
            var config = await _configStore.GetConfigAsync(cancellationToken);
            return Ok(ConfigRedaction.Redact(config.Providers ?? []));
        }

        /// <summary>Gets a single provider by its id (API key redacted).</summary>
        [HttpGet("{providerId}")]
        [ProducesResponseType<ProviderDefinition>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ProviderDefinition>> GetById(string providerId, CancellationToken cancellationToken)
        {
            var provider = await _configStore.GetProviderAsync(providerId, cancellationToken);
            if (provider is null)
                return NotFound(new { error = $"Provider '{providerId}' not found." });

            return Ok(ConfigRedaction.Redact(provider));
        }
    }
}
