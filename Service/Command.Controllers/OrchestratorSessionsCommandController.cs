using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Contract;

namespace OpenAgentOrchestrator.Service.Command.Controllers
{
    /// <summary>
    /// Executes and resumes sessions for any orchestrator defined in <c>config.yaml</c>. The
    /// execute endpoint is identified by the <c>orchestratorId</c> route segment; the resume
    /// endpoint only needs <c>sessionId</c> - it resolves the orchestrator itself from the
    /// session's durable <c>WorkflowCheckpoint</c> row (see <see cref="IWorkflowCheckpointStore"/>),
    /// since checkpointing (and therefore that row) is required for any session that can pause on
    /// a human-in-the-loop request.
    /// </summary>
    [ApiVersion("1.0")]
    [Route("command/api/v{version:apiVersion}/orchestrators/{orchestratorId}/$execute")]
    public sealed class OrchestratorSessionsCommandController : ControllerBase
    {
        private readonly IConfigStore _configStore;
        private readonly IWorkflowExecutionCoordinator _coordinator;
        private readonly IWorkflowCheckpointStore _checkpointStore;

        public OrchestratorSessionsCommandController(IConfigStore configStore, IWorkflowExecutionCoordinator coordinator, IWorkflowCheckpointStore checkpointStore)
        {
            _configStore = configStore;
            _coordinator = coordinator;
            _checkpointStore = checkpointStore;
        }

        /// <summary>Starts a new session (or continues an existing one via <c>sessionId</c>).</summary>
        [HttpPost]
        [Consumes("application/json", "multipart/form-data")]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status500InternalServerError)]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<ExecuteResponse>> Execute(string orchestratorId, CancellationToken cancellationToken)
        {
            var orchestrator = _configStore.GetOrchestrator(orchestratorId);
            if (orchestrator is null)
                return NotFound(new { error = $"Orchestrator '{orchestratorId}' is not configured." });

            var parseResult = await ParseExecuteRequestAsync(cancellationToken);
            if (!parseResult.IsValid)
                return BadRequest(new { error = parseResult.Error });

            try
            {
                var response = await _coordinator.ExecuteAsync(orchestrator, parseResult.Request!);
                return Ok(response);
            }
            catch (OrchestratorExecutionException ex)
            {
                return ToErrorResult(ex);
            }
        }

        /// <summary>Resumes a checkpointed session from its last durable checkpoint.</summary>
        [HttpPost("/command/api/v{version:apiVersion}/sessions/{sessionId}/$resume")]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status500InternalServerError)]
        [ProducesResponseType<ExecuteResponse>(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<ExecuteResponse>> Resume(
            string sessionId, [FromBody] ResumeRequest request, CancellationToken cancellationToken)
        {
            var checkpoint = await _checkpointStore.LoadAsync(sessionId, cancellationToken);
            if (checkpoint is null)
                return NotFound(new { error = $"No checkpoint found for session '{sessionId}'." });

            var orchestrator = _configStore.GetOrchestrator(checkpoint.OrchestratorId);
            if (orchestrator is null)
                return NotFound(new { error = $"Orchestrator '{checkpoint.OrchestratorId}' is not configured." });

            try
            {
                var response = await _coordinator.ResumeAsync(orchestrator, sessionId, request);
                return Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (OrchestratorExecutionException ex)
            {
                return ToErrorResult(ex);
            }
        }

        /// <summary>
        /// Maps a classified execution/resume failure to the appropriate HTTP status code,
        /// preserving the existing <see cref="ExecuteResponse"/> body shape (with
        /// <c>status: "failed"</c>) instead of returning a misleading 200 OK.
        /// </summary>
        private ActionResult<ExecuteResponse> ToErrorResult(OrchestratorExecutionException ex)
        {
            var body = new ExecuteResponse
            {
                SessionId = ex.SessionId,
                Status = "failed",
                Error = ex.Message,
                Steps = []
            };

            var statusCode = ex.Category switch
            {
                // A downstream LLM provider or MCP tool call failed - the caller's request was
                // fine, a dependency wasn't.
                OrchestratorErrorCategory.UpstreamDependency => StatusCodes.Status502BadGateway,

                // Configuration problems and anything unexpected are server-side faults.
                _ => StatusCodes.Status500InternalServerError
            };

            return StatusCode(statusCode, body);
        }

        private static async Task<string> ReadUtf8FileAsync(IFormFile file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);

            return await reader.ReadToEndAsync(cancellationToken);
        }

        private async Task<(bool IsValid, string? Error, ExecuteRequest? Request)> ParseExecuteRequestAsync(CancellationToken cancellationToken)
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(cancellationToken);
                var input = form["input"].ToString();
                var file = form.Files.GetFile("file");

                var hasInput = !string.IsNullOrWhiteSpace(input);
                var hasFile = file is not null;

                if (hasInput && hasFile)
                    return (false, "Provide either input text or a file, not both.", null);

                if (!hasInput && !hasFile)
                    return (false, "Provide either input text or a file.", null);

                string normalizedInput;
                if (hasFile)
                {
                    if (file!.Length == 0)
                        return (false, "Uploaded file is empty.", null);

                    try
                    {
                        normalizedInput = await ReadUtf8FileAsync(file, cancellationToken);
                    }
                    catch (DecoderFallbackException)
                    {
                        return (false, "Uploaded file must be valid UTF-8 text.", null);
                    }

                    if (string.IsNullOrWhiteSpace(normalizedInput))
                        return (false, "Uploaded file contains no usable text input.", null);
                }
                else
                {
                    normalizedInput = input;
                }

                var sessionId = form["sessionId"].ToString();
                var context = ParseContext(form);

                return (true, null, new ExecuteRequest
                {
                    Input = normalizedInput,
                    SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                    Context = context?.Count > 0 ? context : null
                });
            }

            var contentType = Request.ContentType ?? string.Empty;
            if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                return (false, "Content-Type must be application/json or multipart/form-data.", null);

            try
            {
                var request = await JsonSerializer.DeserializeAsync<ExecuteRequest>(
                    Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);

                if (request is null || string.IsNullOrWhiteSpace(request.Input))
                    return (false, "Input is required when executing with JSON payload.", null);

                return (true, null, request);
            }
            catch (JsonException)
            {
                return (false, "Invalid JSON payload.", null);
            }
        }

        private static Dictionary<string, string>? ParseContext(IFormCollection form)
        {
            Dictionary<string, string>? context = null;

            foreach (var (key, value) in form)
            {
                if (!key.StartsWith("context[", StringComparison.Ordinal) || !key.EndsWith("]", StringComparison.Ordinal))
                    continue;

                var contextKey = key.Substring("context[".Length, key.Length - "context[".Length - 1);
                if (string.IsNullOrWhiteSpace(contextKey))
                    continue;

                context ??= new Dictionary<string, string>(StringComparer.Ordinal);
                context[contextKey] = value.ToString();
            }

            return context;
        }
    }
}
