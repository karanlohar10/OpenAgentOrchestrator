using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Application.Tools;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Agents
{
    public sealed class AgentFactory : IAgentFactory
    {
        private readonly IChatClientFactory _chatClientFactory;
        private readonly IToolBinderFactory _toolBinderFactory;
        private readonly IShellToolFactory _shellToolFactory;
        private readonly IConfigStore _configStore;
        private readonly AgentDefaults _agentDefaults;
        private readonly ObservabilityOptions _observability;
        private readonly ILogger<AgentFactory> _logger;

        public AgentFactory(
            IChatClientFactory chatClientFactory,
            IToolBinderFactory toolBinderFactory,
            IShellToolFactory shellToolFactory,
            IConfigStore configStore,
            IOptions<AgentDefaults> agentDefaults,
            IOptions<ObservabilityOptions> observability,
            ILogger<AgentFactory> logger)
        {
            _chatClientFactory = chatClientFactory;
            _toolBinderFactory = toolBinderFactory;
            _shellToolFactory = shellToolFactory;
            _configStore = configStore;
            _agentDefaults = agentDefaults.Value;
            _observability = observability.Value;
            _logger = logger;
        }

        public async Task<AIAgent> CreateAgentAsync(
            AgentDefinition agentDef,
            CancellationToken cancellationToken = default)
        {
            var model = ResolveModel(agentDef);
            var provider = ResolveProvider(agentDef);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Creating agent '{AgentName}' with provider '{ProviderId}' ({ProviderType}), model '{Model}', type '{AgentType}'",
                    agentDef.Name, provider.Id, provider.Type, model, agentDef.AgentType);
            }

            IChatClient chatClient = _chatClientFactory.Create(provider, model);

            // Wrap with retry-with-backoff for HTTP 429 (rate limit) errors, applied before the
            // agent's own function-invocation pipeline so every individual provider call made
            // during a tool-calling loop is retried independently, not just the outer call.
            chatClient = new RetryingChatClient(chatClient, logger: _logger);

            var tools = new List<AITool>();
            if (agentDef.Tools is { Count: > 0 })
            {
                var boundTools = await _toolBinderFactory.BindToolsAsync(agentDef.Tools, cancellationToken);
                tools.AddRange(boundTools);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Bound {ToolCount} MCP tools for agent '{AgentName}'", boundTools.Count, agentDef.Name);
                }
            }

            IList<AIContextProvider>? contextProviders = null;
            if (agentDef.ShellTool is { Enabled: true } shellToolDef)
            {
                // NOTE: the shell executor is created fresh per agent instantiation and is not
                // explicitly disposed - acceptable for this hackathon-scale service, but a
                // long-running production deployment should track and dispose it alongside the
                // agent/workflow session lifetime.
                var shellBinding = _shellToolFactory.Create(shellToolDef);
                tools.Add(shellBinding.Tool);
                contextProviders = [shellBinding.ContextProvider];

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Attached shell tool (mode '{Mode}', requireApproval={RequireApproval}) to agent '{AgentName}'",
                        shellToolDef.Mode, shellToolDef.RequireApproval, agentDef.Name);
                }
            }

            var instructions = agentDef.Instructions
                ?? throw new InvalidOperationException($"Agent '{agentDef.Name}' is missing resolved instructions.");

            var responseFormat = BuildResponseFormat(agentDef);

            var chatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools.Count > 0 ? tools : null,
                ResponseFormat = responseFormat
            };

            return string.Equals(agentDef.AgentType, "harness", StringComparison.OrdinalIgnoreCase)
                ? CreateHarnessAgent(agentDef, chatClient, chatOptions, contextProviders, _observability)
                : CreateChatAgent(agentDef, chatClient, chatOptions, contextProviders, _observability);
        }

        /// <summary>
        /// Builds a plain <see cref="ChatClientAgent"/> - the default agent runtime.
        /// </summary>
        /// <remarks>
        /// Always constructed via <see cref="ChatClientAgentOptions"/> with an explicit,
        /// deterministic <c>Id</c> (the agent's config-defined name). MAF's workflow ExecutorId
        /// for an agent-hosted executor is derived from both <c>AIAgent.Name</c> and
        /// <c>AIAgent.Id</c> (see <c>WorkflowEngine.ComputeExecutorId</c>). If Id were left to its
        /// random default, a freshly-recreated agent (e.g. when rehydrating a human-in-the-loop
        /// checkpoint) would get a different ExecutorId than the one the checkpoint was captured
        /// under, and MAF would reject the checkpoint as incompatible with the workflow. Using the
        /// agent's Name as its Id keeps ExecutorIds stable across process/agent re-creation as
        /// long as config.yaml doesn't change.
        /// </remarks>
        /// <remarks>
        /// Instrumented at the agent boundary only (not the raw chat client) via
        /// <c>AIAgent.AsBuilder().UseOpenTelemetry(...)</c>, per the Microsoft Agent Framework
        /// observability guidance that instrumenting both layers produces duplicate span data.
        /// </remarks>
        private static AIAgent CreateChatAgent(
            AgentDefinition agentDef, IChatClient chatClient, ChatOptions chatOptions, IList<AIContextProvider>? contextProviders,
            ObservabilityOptions observability)
        {
            AIAgent agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
            {
                Id = agentDef.Name,
                Name = agentDef.Name,
                ChatOptions = chatOptions,
                AIContextProviders = contextProviders
            });

            return agent.AsBuilder()
                .UseOpenTelemetry(observability.AgentSourceName, cfg => cfg.EnableSensitiveData = observability.EnableSensitiveData)
                .Build();
        }

        /// <summary>
        /// Builds a Microsoft Agent Framework Agent Harness agent (see
        /// https://learn.microsoft.com/agent-framework/concepts/harness) via
        /// <c>IChatClient.AsHarnessAgent</c>. Opt-in per agent via <c>agentType: harness</c> in
        /// config.yaml.
        /// </summary>
        /// <remarks>
        /// Setting <see cref="HarnessAgentOptions.OpenTelemetrySourceName"/> auto-instruments
        /// *both* the harness's internal chat-client and agent boundary from the single source
        /// name - no separate <c>UseOpenTelemetry</c> call is needed (and adding one would
        /// duplicate spans). Sensitive-data capture for the harness path is controlled process-
        /// wide via the standard <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>
        /// environment variable (set once at startup in <c>Program.cs</c> from
        /// <see cref="ObservabilityOptions.EnableSensitiveData"/>), since
        /// <see cref="HarnessAgentOptions"/> has no dedicated sensitive-data flag.
        /// </remarks>
        private static AIAgent CreateHarnessAgent(
            AgentDefinition agentDef, IChatClient chatClient, ChatOptions chatOptions, IList<AIContextProvider>? contextProviders,
            ObservabilityOptions observability)
        {
            var harnessDef = agentDef.Harness ?? new HarnessOptionsDefinition();

#pragma warning disable MAAI001 // MaxContextWindowTokens/MaxOutputTokens are experimental in Microsoft.Agents.AI.Harness.
            return chatClient.AsHarnessAgent(new HarnessAgentOptions
            {
                Id = agentDef.Name,
                Name = agentDef.Name,
                ChatOptions = chatOptions,
                HarnessInstructions = harnessDef.HarnessInstructions,
                MaxContextWindowTokens = harnessDef.MaxContextWindowTokens,
                MaxOutputTokens = harnessDef.MaxOutputTokens,
                AIContextProviders = contextProviders,
                OpenTelemetrySourceName = observability.AgentSourceName
            });
#pragma warning restore MAAI001
        }

        private static ChatResponseFormat? BuildResponseFormat(AgentDefinition agentDef)
        {
            var config = agentDef.ResponseFormat;
            if (config is null)
                return null;

            return config.Type switch
            {
                "text" => ChatResponseFormat.Text,
                "json_object" => ChatResponseFormat.Json,
                "json_schema" => BuildJsonSchemaResponseFormat(agentDef.Name, config),
                _ => throw new InvalidOperationException(
                    $"Agent '{agentDef.Name}': unsupported responseFormat type '{config.Type}'. Must be one of: text, json_object, json_schema.")
            };
        }

        private static ChatResponseFormatJson BuildJsonSchemaResponseFormat(string agentName, ResponseFormatDefinition config)
        {
            if (string.IsNullOrWhiteSpace(config.Schema))
                throw new InvalidOperationException($"Agent '{agentName}': responseFormat type 'json_schema' requires a non-empty schema.");

            JsonElement schemaElement;
            try
            {
                using var document = JsonDocument.Parse(config.Schema);
                schemaElement = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Agent '{agentName}': responseFormat schema is not valid JSON: {ex.Message}", ex);
            }

            return ChatResponseFormat.ForJsonSchema(schemaElement);
        }

        private string ResolveModel(AgentDefinition agentDef)
        {
            if (!string.IsNullOrWhiteSpace(agentDef.Model))
                return agentDef.Model;

            if (!string.IsNullOrWhiteSpace(_agentDefaults.DefaultModel))
                return _agentDefaults.DefaultModel;

            throw new InvalidOperationException(
                $"Agent '{agentDef.Name}' does not specify a model and no AgentDefaults.DefaultModel is configured.");
        }

        private ProviderDefinition ResolveProvider(AgentDefinition agentDef)
        {
            var providerId = !string.IsNullOrWhiteSpace(agentDef.Provider)
                ? agentDef.Provider
                : _agentDefaults.DefaultProvider;

            if (string.IsNullOrWhiteSpace(providerId))
                throw new InvalidOperationException(
                    $"Agent '{agentDef.Name}' does not specify a provider and no AgentDefaults.DefaultProvider is configured.");

            var provider = _configStore.GetConfig().Providers?.FirstOrDefault(p =>
                p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
                throw new InvalidOperationException(
                    $"Provider '{providerId}' is not defined in the providers section.");

            return provider;
        }
    }
}
