namespace OpenAgentOrchestrator.Query.Domain.Model
{
    public sealed class OrchestratorSummaryResponse
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }
        public required string Pattern { get; set; }
        public int AgentCount { get; set; }
    }
}
