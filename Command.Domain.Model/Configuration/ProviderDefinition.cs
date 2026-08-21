namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    public sealed class ProviderDefinition
    {
        public required string Id { get; set; }

        /// <summary>azure-openai</summary>
        public required string Type { get; set; }

        public string? Endpoint { get; set; }

        /// <summary>
        /// The provider's API key, stored directly in <c>config.yaml</c>. <c>config.yaml</c> is
        /// gitignored - never commit real values here. Use <c>config.sample.yaml</c> (placeholder
        /// values only) as the tracked template.
        /// </summary>
        public string? ApiKey { get; set; }
    }
}
