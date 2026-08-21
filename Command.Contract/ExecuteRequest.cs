namespace OpenAgentOrchestrator.Command.Contract
{
    public sealed class ExecuteRequest
    {
        public required string Input { get; set; }
        public Dictionary<string, string>? Context { get; set; }

        /// <summary>Existing session id to continue (optional).</summary>
        public string? SessionId { get; set; }
    }
}
