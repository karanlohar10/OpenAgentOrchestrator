using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    public interface IConfigMerge
    {
        /// <summary>
        /// Reconciles secret fields on <paramref name="candidate"/> (a config that was likely
        /// round-tripped through a UI that only ever saw <see cref="ConfigRedaction"/>-redacted
        /// values) against the real values in <paramref name="previous"/> (the currently-loaded,
        /// last-known-good config). Any secret field on <paramref name="candidate"/> that is
        /// blank or still equal to <see cref="ConfigRedaction.RedactedPlaceholder"/> is replaced
        /// in-place with the matching entity's real value from <paramref name="previous"/>
        /// (matched by natural key - provider id; orchestrator id + agent name + tool
        /// name/header key). Entities that don't exist in <paramref name="previous"/> (newly
        /// added via the editor) cannot "inherit" a secret this way - a blank/placeholder secret
        /// on a genuinely new entity is reported as an error instead, since there is nothing to
        /// fall back to.
        /// </summary>
        /// <returns>Errors for secrets that could not be resolved (new entity, no real value supplied).</returns>
        List<string> MergeSecrets(PlatformConfig previous, PlatformConfig candidate);
    }

    public sealed class ConfigMerge : IConfigMerge
    {
        public List<string> MergeSecrets(PlatformConfig previous, PlatformConfig candidate)
        {
            var errors = new List<string>();

            MergeProviderSecrets(previous, candidate, errors);
            MergeOrchestratorSecrets(previous, candidate, errors);

            return errors;
        }

        private static void MergeProviderSecrets(PlatformConfig previous, PlatformConfig candidate, List<string> errors)
        {
            if (candidate.Providers is not { Count: > 0 })
                return;

            var previousProviders = (previous.Providers ?? [])
                .ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var provider in candidate.Providers)
            {
                if (!IsUnresolvedSecret(provider.ApiKey))
                    continue;

                if (previousProviders.TryGetValue(provider.Id, out var existing))
                    provider.ApiKey = existing.ApiKey;
                else if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                    errors.Add($"Provider '{provider.Id}': apiKey still contains the redacted placeholder - please enter a real value.");
                // A brand new provider with a blank apiKey is left as-is; ConfigValidator only
                // warns (doesn't error) on a missing provider apiKey, so no error here either.
            }
        }

        private static void MergeOrchestratorSecrets(PlatformConfig previous, PlatformConfig candidate, List<string> errors)
        {
            var previousOrchestrators = previous.Orchestrators
                .ToDictionary(o => o.Id, o => o, StringComparer.OrdinalIgnoreCase);

            foreach (var orchestrator in candidate.Orchestrators)
            {
                previousOrchestrators.TryGetValue(orchestrator.Id, out var previousOrchestrator);
                var previousAgents = (previousOrchestrator?.Agents ?? [])
                    .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

                foreach (var agent in orchestrator.Agents)
                {
                    previousAgents.TryGetValue(agent.Name, out var previousAgent);
                    var scope = $"Orchestrator '{orchestrator.Id}', agent '{agent.Name}'";

                    MergeToolSecrets(previousAgent, agent, scope, errors);
                }
            }
        }

        private static void MergeToolSecrets(AgentDefinition? previousAgent, AgentDefinition agent, string scope, List<string> errors)
        {
            if (agent.Tools is not { Count: > 0 })
                return;

            var previousTools = (previousAgent?.Tools ?? [])
                .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);

            foreach (var toolItem in agent.Tools)
            {
                previousTools.TryGetValue(toolItem.Name, out var previousTool);
                var toolScope = $"{scope}, tool '{toolItem.Name}'";

                if (IsUnresolvedSecret(toolItem.ClientSecret))
                {
                    if (previousTool is not null)
                        toolItem.ClientSecret = previousTool.ClientSecret;
                    else if (!string.IsNullOrWhiteSpace(toolItem.ClientSecret))
                        errors.Add($"{toolScope}: clientSecret still contains the redacted placeholder - please enter a real value.");
                }

                if (IsUnresolvedSecret(toolItem.ApiKey))
                {
                    if (previousTool is not null)
                        toolItem.ApiKey = previousTool.ApiKey;
                    else if (!string.IsNullOrWhiteSpace(toolItem.ApiKey))
                        errors.Add($"{toolScope}: apiKey still contains the redacted placeholder - please enter a real value.");
                }

                if (toolItem.Headers is not { Count: > 0 })
                    continue;

                foreach (var headerName in toolItem.Headers.Keys.ToList())
                {
                    var headerValue = toolItem.Headers[headerName];
                    if (!IsUnresolvedSecret(headerValue))
                        continue;

                    if (previousTool?.Headers is not null && previousTool.Headers.TryGetValue(headerName, out var previousValue))
                        toolItem.Headers[headerName] = previousValue;
                    else if (!string.IsNullOrWhiteSpace(headerValue))
                        errors.Add($"{toolScope}: header '{headerName}' still contains the redacted placeholder - please enter a real value.");
                }
            }
        }

        private static bool IsUnresolvedSecret(string? value) =>
            string.IsNullOrWhiteSpace(value) || value == ConfigRedaction.RedactedPlaceholder;
    }
}
