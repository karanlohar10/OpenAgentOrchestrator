namespace OpenAgentOrchestrator.Command.Contract
{
    public sealed class ExecuteResponse
    {
        public required string SessionId { get; set; }

        /// <summary>completed | pending_approval | failed | rejected</summary>
        public required string Status { get; set; }

        public string? Output { get; set; }
        public List<AgentStepResult>? Steps { get; set; }
        public string? Error { get; set; }
    }

    public sealed class AgentStepResult
    {
        public required string AgentName { get; set; }
        public required string Status { get; set; }
        public string? Output { get; set; }
        public double DurationMs { get; set; }
    }
}
