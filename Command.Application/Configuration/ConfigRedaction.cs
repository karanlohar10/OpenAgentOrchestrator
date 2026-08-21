using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    /// <summary>
    /// Produces redacted copies of config.yaml-sourced definitions for exposure over read-only
    /// query endpoints. config.yaml stores secrets (API keys, client secrets, header values)
    /// literally - unlike the original DB-backed design (which stored only secret *references*),
    /// so anything read from it must be explicitly redacted before it is ever serialized back out
    /// over HTTP.
    /// </summary>
    public static class ConfigRedaction
    {
        /// <summary>
        /// Placeholder written in place of a real secret value on read-only query endpoints.
        /// Public so <see cref="ConfigMerge"/> can recognize "this field wasn't actually retyped
        /// by the user" on save (the secret-sentinel merge pattern) - see that class for details.
        /// </summary>
        public const string RedactedPlaceholder = "***redacted***";

        public static ProviderDefinition Redact(ProviderDefinition provider) => new()
        {
            Id = provider.Id,
            Type = provider.Type,
            Endpoint = provider.Endpoint,
            ApiKey = string.IsNullOrEmpty(provider.ApiKey) ? provider.ApiKey : RedactedPlaceholder
        };

        public static List<ProviderDefinition> Redact(IEnumerable<ProviderDefinition> providers) =>
            providers.Select(Redact).ToList();

        public static OrchestratorDefinition Redact(OrchestratorDefinition orchestrator) => new()
        {
            Id = orchestrator.Id,
            Name = orchestrator.Name,
            Description = orchestrator.Description,
            Pattern = orchestrator.Pattern,
            Checkpointing = orchestrator.Checkpointing,
            Agents = orchestrator.Agents.Select(Redact).ToList()
        };

        public static AgentDefinition Redact(AgentDefinition agent) => new()
        {
            Name = agent.Name,
            Instructions = agent.Instructions,
            Provider = agent.Provider,
            Model = agent.Model,
            Tools = agent.Tools?.Select(Redact).ToList(),
            ResponseFormat = agent.ResponseFormat,
            AgentType = agent.AgentType,
            Harness = agent.Harness,
            ShellTool = agent.ShellTool,
            WebSearchTool = agent.WebSearchTool is null ? null : Redact(agent.WebSearchTool)
        };

        public static WebSearchToolDefinition Redact(WebSearchToolDefinition webSearchTool) => new()
        {
            Enabled = webSearchTool.Enabled,
            Provider = webSearchTool.Provider,
            ApiKey = string.IsNullOrEmpty(webSearchTool.ApiKey) ? webSearchTool.ApiKey : RedactedPlaceholder,
            MaxResults = webSearchTool.MaxResults,
            SearchDepth = webSearchTool.SearchDepth,
            SearchEngineId = webSearchTool.SearchEngineId,
            SearchEngine = webSearchTool.SearchEngine
        };

        public static ToolDefinition Redact(ToolDefinition tool) => new()
        {
            Type = tool.Type,
            Name = tool.Name,
            Endpoint = tool.Endpoint,
            AuthType = tool.AuthType,
            TokenEndpoint = tool.TokenEndpoint,
            ClientId = tool.ClientId,
            ClientSecret = string.IsNullOrEmpty(tool.ClientSecret) ? tool.ClientSecret : RedactedPlaceholder,
            Scope = tool.Scope,
            Headers = tool.Headers?.ToDictionary(kv => kv.Key, _ => RedactedPlaceholder)
        };
    }
}
