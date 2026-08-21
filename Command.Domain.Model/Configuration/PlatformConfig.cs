namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    public sealed class PlatformConfig
    {
        public List<ProviderDefinition>? Providers { get; set; }
        public required List<OrchestratorDefinition> Orchestrators { get; set; }
    }
}
