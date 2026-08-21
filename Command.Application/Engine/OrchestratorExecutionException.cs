namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// Broad classification of an orchestrator execution/resume failure, used to select the
    /// HTTP status code returned to the caller (see
    /// <see cref="Service.Command.Controllers.OrchestratorSessionsCommandController"/>).
    /// </summary>
    public enum OrchestratorErrorCategory
    {
        /// <summary>
        /// A server-side setup problem (e.g. a missing provider API key) - not the caller's
        /// fault. Maps to HTTP 500.
        /// </summary>
        Configuration,

        /// <summary>
        /// A downstream dependency (an LLM provider or an MCP tool endpoint) failed or refused
        /// the call (e.g. a 401 from an MCP server). Maps to HTTP 502.
        /// </summary>
        UpstreamDependency,

        /// <summary>Anything else. Maps to HTTP 500.</summary>
        Unexpected
    }

    /// <summary>
    /// Wraps an orchestrator execution/resume failure after the failing session and its
    /// checkpoint (if any) have already been persisted with <c>Status = "failed"</c>, carrying
    /// enough information for the controller to translate it into the correct HTTP status code
    /// instead of a 200 OK.
    /// </summary>
    public sealed class OrchestratorExecutionException : Exception
    {
        public string SessionId { get; }
        public OrchestratorErrorCategory Category { get; }

        public OrchestratorExecutionException(string sessionId, OrchestratorErrorCategory category, string message, Exception innerException)
            : base(message, innerException)
        {
            SessionId = sessionId;
            Category = category;
        }

        /// <summary>
        /// Classifies an exception raised while executing/resuming an orchestrator workflow into
        /// an <see cref="OrchestratorErrorCategory"/>.
        /// </summary>
        public static OrchestratorErrorCategory Classify(Exception ex) => ex switch
        {
            // Missing/blank provider API keys, endpoints, etc. (see ChatClientFactory) - a
            // server-side configuration problem, not something the caller can fix.
            ArgumentException => OrchestratorErrorCategory.Configuration,

            // A call to an LLM provider or an MCP tool endpoint failed (e.g. non-success status
            // code, network failure) - the caller's request was fine, a downstream dependency
            // wasn't.
            HttpRequestException => OrchestratorErrorCategory.UpstreamDependency,

            _ => OrchestratorErrorCategory.Unexpected
        };
    }
}
