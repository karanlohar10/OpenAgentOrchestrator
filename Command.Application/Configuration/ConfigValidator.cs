using System.Text.Json;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    public interface IConfigValidator
    {
        ValidationResult Validate(PlatformConfig config);
        ValidationResult ValidateOrchestrator(OrchestratorDefinition orchestrator);
        ValidationResult ValidateProvider(ProviderDefinition provider);
        ValidationResult ValidateAgent(OrchestratorDefinition orchestrator, AgentDefinition agent);
        ValidationResult ValidateTool(OrchestratorDefinition orchestrator, AgentDefinition agent, ToolDefinition tool);
    }

    /// <summary>
    /// Validates <see cref="PlatformConfig"/> and individual entities. Only the "sequential"
    /// pattern and "mcp" tool type are accepted.
    /// </summary>
    public sealed class ConfigValidator : IConfigValidator
    {
        private static readonly HashSet<string> ValidPatterns = ["sequential"];
        private static readonly HashSet<string> ValidToolTypes = ["mcp"];
        private static readonly HashSet<string> ValidProviderTypes = ["azure-openai"];
        private static readonly HashSet<string> ValidResponseFormatTypes = ["json_schema", "json_object", "text"];
        private static readonly HashSet<string> ValidAgentTypes = ["chat", "harness"];
        private static readonly HashSet<string> ValidShellToolModes = ["stateless", "persistent"];
        private static readonly HashSet<string> ValidWebSearchProviders = ["tavily", "bing", "google", "serpapi"];

        private readonly AgentDefaults _agentDefaults;

        public ConfigValidator(AgentDefaults? agentDefaults = null)
        {
            _agentDefaults = agentDefaults ?? new AgentDefaults();
        }

        public ValidationResult Validate(PlatformConfig config)
        {
            var result = new ValidationResult { IsValid = true };

            var providerIds = ValidateProviders(config, result);

            ValidateOrchestrators(config, providerIds, result);

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        public ValidationResult ValidateProvider(ProviderDefinition provider)
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(provider.Id))
                result.Errors.Add("Provider id is required.");

            if (!ValidProviderTypes.Contains(provider.Type))
                result.Errors.Add($"Provider '{provider.Id}': invalid type '{provider.Type}'. Must be one of: {string.Join(", ", ValidProviderTypes)}.");

            if (provider.Type is "azure-openai" && string.IsNullOrWhiteSpace(provider.Endpoint))
                result.Errors.Add($"Provider '{provider.Id}': azure-openai type requires an endpoint.");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        public ValidationResult ValidateAgent(OrchestratorDefinition orchestrator, AgentDefinition agent)
        {
            var result = new ValidationResult { IsValid = true };
            ValidateAgentInternal(orchestrator, agent, result);
            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        public ValidationResult ValidateTool(OrchestratorDefinition orchestrator, AgentDefinition agent, ToolDefinition tool)
        {
            var result = new ValidationResult { IsValid = true };
            ValidateToolInternal(orchestrator, agent, tool, result);
            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private static HashSet<string> ValidateProviders(PlatformConfig config, ValidationResult result)
        {
            var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Providers is not { Count: > 0 })
                return providerIds;

            foreach (var provider in config.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.Id))
                    result.Errors.Add("Provider id is required.");
                else if (!providerIds.Add(provider.Id))
                    result.Errors.Add($"Duplicate provider id: '{provider.Id}'.");

                if (!ValidProviderTypes.Contains(provider.Type))
                    result.Errors.Add($"Provider '{provider.Id}': invalid type '{provider.Type}'. Must be one of: {string.Join(", ", ValidProviderTypes)}.");

                if (provider.Type is "azure-openai" && string.IsNullOrWhiteSpace(provider.Endpoint))
                    result.Errors.Add($"Provider '{provider.Id}': azure-openai type requires an endpoint.");

                if (string.IsNullOrWhiteSpace(provider.ApiKey))
                    result.Warnings.Add($"Provider '{provider.Id}': no apiKey configured.");
            }

            return providerIds;
        }

        private void ValidateOrchestrators(PlatformConfig config, HashSet<string> providerIds, ValidationResult result)
        {
            if (config.Orchestrators is null || config.Orchestrators.Count == 0)
            {
                result.Errors.Add("At least one orchestrator must be defined.");
                return;
            }

            var ids = new HashSet<string>();
            foreach (var orch in config.Orchestrators)
            {
                if (!ids.Add(orch.Id))
                    result.Errors.Add($"Duplicate orchestrator id: '{orch.Id}'.");

                ValidateCheckpointing(orch.Checkpointing, $"Orchestrator '{orch.Id}'", result);

                foreach (var agent in orch.Agents)
                {
                    if (!string.IsNullOrWhiteSpace(agent.Provider) && providerIds.Count > 0 && !providerIds.Contains(agent.Provider))
                        result.Errors.Add($"Orchestrator '{orch.Id}', agent '{agent.Name}': provider '{agent.Provider}' is not defined in the providers section.");
                }

                var orchResult = ValidateOrchestrator(orch);
                result.Errors.AddRange(orchResult.Errors);
                result.Warnings.AddRange(orchResult.Warnings);
            }
        }

        public ValidationResult ValidateOrchestrator(OrchestratorDefinition orchestrator)
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(orchestrator.Id))
                result.Errors.Add("Orchestrator id is required.");

            if (string.IsNullOrWhiteSpace(orchestrator.Name))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}': name is required.");

            if (!ValidPatterns.Contains(orchestrator.Pattern))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}': invalid pattern '{orchestrator.Pattern}'. Must be one of: {string.Join(", ", ValidPatterns)}.");

            if (orchestrator.Agents is null || orchestrator.Agents.Count == 0)
                result.Errors.Add($"Orchestrator '{orchestrator.Id}': at least one agent is required.");
            else
            {
                foreach (var agent in orchestrator.Agents)
                    ValidateAgentInternal(orchestrator, agent, result);
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private void ValidateAgentInternal(OrchestratorDefinition orchestrator, AgentDefinition agent, ValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}': agent name is required.");

            if (string.IsNullOrWhiteSpace(agent.Instructions))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': instructions are required.");

            if (string.IsNullOrWhiteSpace(agent.Provider) && string.IsNullOrWhiteSpace(_agentDefaults.DefaultProvider))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': provider is required (no provider specified and no AgentDefaults.DefaultProvider is configured).");

            if (string.IsNullOrWhiteSpace(agent.Model) && string.IsNullOrWhiteSpace(_agentDefaults.DefaultModel))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': model is required (no model specified and no AgentDefaults.DefaultModel is configured).");

            if (agent.Tools != null)
            {
                foreach (var tool in agent.Tools)
                    ValidateToolInternal(orchestrator, agent, tool, result);
            }

            if (agent.ResponseFormat != null)
                ValidateResponseFormat(orchestrator, agent, result);

            ValidateAgentType(orchestrator, agent, result);

            if (agent.ShellTool is { Enabled: true } shellTool)
                ValidateShellTool(orchestrator, agent, shellTool, result);

            if (agent.WebSearchTool is { Enabled: true } webSearchTool)
                ValidateWebSearchTool(orchestrator, agent, webSearchTool, result);
        }

        private static void ValidateAgentType(OrchestratorDefinition orchestrator, AgentDefinition agent, ValidationResult result)
        {
            if (!ValidAgentTypes.Contains(agent.AgentType))
            {
                result.Errors.Add(
                    $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': invalid agentType '{agent.AgentType}'. Must be one of: {string.Join(", ", ValidAgentTypes)}.");
            }
        }

        private static void ValidateShellTool(OrchestratorDefinition orchestrator, AgentDefinition agent, ShellToolDefinition shellTool, ValidationResult result)
        {
            var prefix = $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}', shellTool";

            if (!ValidShellToolModes.Contains(shellTool.Mode))
                result.Errors.Add($"{prefix}: invalid mode '{shellTool.Mode}'. Must be one of: {string.Join(", ", ValidShellToolModes)}.");

            if (!shellTool.AcknowledgeUnsafe)
            {
                result.Errors.Add(
                    $"{prefix}: enabled but 'acknowledgeUnsafe: true' was not set. Shell execution can modify " +
                    "files, launch processes, and access credentials/network on the host - this must be explicitly acknowledged.");
            }
        }

        private static void ValidateWebSearchTool(OrchestratorDefinition orchestrator, AgentDefinition agent, WebSearchToolDefinition webSearchTool, ValidationResult result)
        {
            var prefix = $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}', webSearchTool";

            if (!ValidWebSearchProviders.Contains(webSearchTool.Provider.ToLowerInvariant()))
                result.Errors.Add($"{prefix}: invalid provider '{webSearchTool.Provider}'. Must be one of: {string.Join(", ", ValidWebSearchProviders)}.");

            if (string.IsNullOrWhiteSpace(webSearchTool.ApiKey))
                result.Errors.Add($"{prefix}: apiKey is required.");

            if (string.Equals(webSearchTool.Provider, "google", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(webSearchTool.SearchEngineId))
            {
                result.Errors.Add($"{prefix}: provider 'google' requires searchEngineId (the Custom Search Engine 'cx' id).");
            }
        }

        private static void ValidateToolInternal(OrchestratorDefinition orchestrator, AgentDefinition agent, ToolDefinition tool, ValidationResult result)
        {
            if (!ValidToolTypes.Contains(tool.Type))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': invalid tool type '{tool.Type}'. Must be one of: {string.Join(", ", ValidToolTypes)}.");

            if (tool.Type == "mcp" && string.IsNullOrWhiteSpace(tool.Endpoint))
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}', tool '{tool.Name}': MCP tools require an endpoint.");

            if (string.Equals(tool.AuthType, "bearer", StringComparison.OrdinalIgnoreCase))
                ValidateBearerAuth(orchestrator, agent, tool, result);
        }

        private static void ValidateBearerAuth(OrchestratorDefinition orchestrator, AgentDefinition agent, ToolDefinition tool, ValidationResult result)
        {
            var prefix = $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}', tool '{tool.Name}'";
            if (string.IsNullOrWhiteSpace(tool.TokenEndpoint))
                result.Errors.Add($"{prefix}: bearer auth requires a tokenEndpoint.");
            if (string.IsNullOrWhiteSpace(tool.ClientId))
                result.Errors.Add($"{prefix}: bearer auth requires a clientId.");
            if (string.IsNullOrWhiteSpace(tool.ClientSecret))
                result.Errors.Add($"{prefix}: bearer auth requires a clientSecret.");
        }

        private static void ValidateResponseFormat(OrchestratorDefinition orchestrator, AgentDefinition agent, ValidationResult result)
        {
            var responseFormat = agent.ResponseFormat!;

            if (!ValidResponseFormatTypes.Contains(responseFormat.Type))
            {
                result.Errors.Add(
                    $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': invalid responseFormat type '{responseFormat.Type}'. Must be one of: {string.Join(", ", ValidResponseFormatTypes)}.");
                return;
            }

            if (responseFormat.Type != "json_schema")
                return;

            if (string.IsNullOrWhiteSpace(responseFormat.Schema))
            {
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': responseFormat type 'json_schema' requires a non-empty schema.");
                return;
            }

            try
            {
                using var _ = JsonDocument.Parse(responseFormat.Schema);
            }
            catch (JsonException ex)
            {
                result.Errors.Add($"Orchestrator '{orchestrator.Id}', agent '{agent.Name}': responseFormat schema is not valid JSON ({ex.Message}).");
            }
        }

        private static void ValidateCheckpointing(CheckpointingDefinition checkpointing, string scopeLabel, ValidationResult result)
        {
            if (checkpointing.HumanInLoop?.Enabled == true && !checkpointing.Enabled)
                result.Errors.Add($"{scopeLabel}: humanInLoop is enabled but checkpointing is not - checkpointing must be enabled for human-in-the-loop resume to work.");
        }
    }
}
