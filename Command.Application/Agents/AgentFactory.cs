using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Agents
{
    public sealed class AgentFactory : IAgentFactory
    {
        private readonly IChatClientFactory _chatClientFactory;
        private readonly IToolBinderFactory _toolBinderFactory;
        private readonly IConfigStore _configStore;
        private readonly AgentDefaults _agentDefaults;
        private readonly ObservabilityOptions _observability;
        private readonly ILogger<AgentFactory> _logger;

        public AgentFactory(
            IChatClientFactory chatClientFactory,
            IToolBinderFactory toolBinderFactory,
            IConfigStore configStore,
            IOptions<AgentDefaults> agentDefaults,
            IOptions<ObservabilityOptions> observability,
            ILogger<AgentFactory> logger)
        {
            _chatClientFactory = chatClientFactory;
            _toolBinderFactory = toolBinderFactory;
            _configStore = configStore;
            _agentDefaults = agentDefaults.Value;
            _observability = observability.Value;
            _logger = logger;
        }

        /// <summary>
        /// Hardcoded instructions appended to an agent's own instructions when its orchestrator has
        /// <c>checkpointing.humanInLoop.enableClarificationFlag: true</c> - requires the agent to
        /// respond with a fixed JSON envelope so <c>StepReviewExecutor</c> can tell whether the step
        /// is a genuine question needing a human answer, or a routine result. See
        /// <see cref="HumanInLoopDefinition.EnableClarificationFlag"/> and
        /// <see cref="ClarificationEnvelope"/>.
        /// </summary>
        internal const string ClarificationEnvelopeInstructions = """
            You must respond with ONLY a single JSON object of this exact shape, and nothing else \
            (no surrounding text, no markdown code fences):
            {"needsClarification": true|false, "clarificationQuestion": "<string, required only when needsClarification is true>", "content": "<your actual answer/output, as a string>"}
            Set "needsClarification" to true only when you genuinely cannot proceed without a human answering a specific question first - in that case, put the question in "clarificationQuestion" and leave "content" empty or set it to your partial progress so far. Otherwise set "needsClarification" to false and put your complete, real output in "content".
            """;

        public async Task<AIAgent> CreateAgentAsync(
            AgentDefinition agentDef,
            bool requireClarificationEnvelope = false,
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
            IList<AIContextProvider>? contextProviders = null;
            if (agentDef.Tools is { Count: > 0 })
            {
                var bound = await _toolBinderFactory.BindToolsAsync(agentDef.Tools, cancellationToken);
                tools.AddRange(bound.Tools);
                contextProviders = bound.ContextProviders;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Bound {ToolCount} tools for agent '{AgentName}'", bound.Tools.Count, agentDef.Name);
                }
            }

            var instructions = agentDef.Instructions
                ?? throw new InvalidOperationException($"Agent '{agentDef.Name}' is missing resolved instructions.");

            if (requireClarificationEnvelope)
                instructions = $"{instructions}\n\n{ClarificationEnvelopeInstructions}";

            var responseFormat = BuildResponseFormat(agentDef);

            var chatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools.Count > 0 ? tools : null,
                ResponseFormat = responseFormat
            };

            return string.Equals(agentDef.AgentType, "harness", StringComparison.OrdinalIgnoreCase)
                ? CreateHarnessAgent(agentDef, chatClient, chatOptions, contextProviders, _observability)
                : CreateChatAgentWithPlanning(agentDef, chatClient, chatOptions, contextProviders, _observability);
        }

        /// <summary>
        /// Builds a "chat" (<c>ChatClientAgent</c>) agent with optional planning (todos/agent-mode)
        /// support layered on via <see cref="AgentDefinition.Planning"/>, then wraps it in a
        /// bounded <c>LoopAgent</c> when <see cref="PlanningDefinition.EnableTodoLoop"/> is set.
        /// Harness agents get equivalent behavior natively through <c>HarnessAgentOptions</c>
        /// instead (see <see cref="CreateHarnessAgent"/>) - the harness already owns its own
        /// looping/provider pipeline, so wrapping it externally here would double it up.
        /// </summary>
        private static AIAgent CreateChatAgentWithPlanning(
            AgentDefinition agentDef, IChatClient chatClient, ChatOptions chatOptions, IList<AIContextProvider>? contextProviders,
            ObservabilityOptions observability)
        {
            var planning = agentDef.Planning;
            if (planning is { EnableTodos: true } or { EnableAgentMode: true })
            {
                var providers = contextProviders is null ? new List<AIContextProvider>() : new List<AIContextProvider>(contextProviders);

                if (planning.EnableTodos)
                    providers.Add(new TodoProvider());

                if (planning.EnableAgentMode)
                    providers.Add(BuildAgentModeProvider(planning));

                contextProviders = providers;
            }

            AIAgent agent = CreateChatAgent(agentDef, chatClient, chatOptions, contextProviders, observability);

            if (planning is { EnableTodoLoop: true })
                agent = WrapWithTodoCompletionLoop(agent, planning);

            return agent;
        }

        /// <summary>
        /// Wraps <paramref name="agent"/> in a <c>LoopAgent</c> + <c>TodoCompletionLoopEvaluator</c>
        /// so it keeps re-invoking itself (up to a hardcoded 5 iterations - intentionally not
        /// configurable) while incomplete todos remain in one of
        /// <see cref="PlanningDefinition.LoopModes"/> (default: <c>["execute"]</c>). See
        /// https://learn.microsoft.com/agent-framework/agents/planning-and-todos. Note:
        /// <c>LoopAgent</c> is a <c>DelegatingAIAgent</c> whose <c>Id</c>/<c>Name</c> pass through
        /// to the wrapped inner agent, so <c>WorkflowEngine.ComputeExecutorId</c> stability is
        /// unaffected by this wrapping.
        /// </summary>
        private static AIAgent WrapWithTodoCompletionLoop(AIAgent agent, PlanningDefinition planning)
        {
#pragma warning disable MAAI001 // LoopAgent/TodoCompletionLoopEvaluator are experimental in Microsoft.Agents.AI.
            var evaluator = new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions
            {
                Modes = planning.LoopModes ?? ["execute"]
            });

            return new LoopAgent(agent, evaluator, new LoopAgentOptions { MaxIterations = 5 });
#pragma warning restore MAAI001
        }

        /// <summary>
        /// Maps <see cref="PlanningDefinition.DefaultMode"/>/<see cref="PlanningDefinition.Modes"/>
        /// to an <c>AgentModeProvider</c>, falling back to the framework's built-in
        /// <c>plan</c>/<c>execute</c> modes when <see cref="PlanningDefinition.Modes"/> is omitted.
        /// </summary>
        private static AgentModeProvider BuildAgentModeProvider(PlanningDefinition planning) =>
            new(BuildAgentModeProviderOptions(planning));

        private static AgentModeProviderOptions BuildAgentModeProviderOptions(PlanningDefinition planning)
        {
            var options = new AgentModeProviderOptions
            {
                DefaultMode = planning.DefaultMode ?? "plan"
            };

            if (planning.Modes is { Count: > 0 })
            {
                options.Modes = planning.Modes
                    .Select(m => new AgentModeProviderOptions.AgentMode(m.Name, m.Instructions))
                    .ToList();
            }

            return options;
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
            var planning = agentDef.Planning;

            var harnessOptions = new HarnessAgentOptions
            {
                Id = agentDef.Name,
                Name = agentDef.Name,
                ChatOptions = chatOptions,
                HarnessInstructions = harnessDef.HarnessInstructions,
                DisableWebSearch = harnessDef.DisableWebSearch,
                AIContextProviders = contextProviders,
                OpenTelemetrySourceName = observability.AgentSourceName,
                // The harness enables TodoProvider/AgentModeProvider by default - disable them
                // unless explicitly opted into via config, and configure the loop natively
                // (rather than external LoopAgent wrapping, which the chat-agent path uses) since
                // the harness already owns its own provider/loop pipeline internally.
                DisableTodoProvider = planning is not { EnableTodos: true },
                DisableAgentModeProvider = planning is not { EnableAgentMode: true }
            };

            if (planning is { EnableAgentMode: true })
                harnessOptions.AgentModeProviderOptions = BuildAgentModeProviderOptions(planning);

            if (planning is { EnableTodoLoop: true })
            {
#pragma warning disable MAAI001 // LoopEvaluators/LoopAgentOptions/TodoCompletionLoopEvaluator are experimental.
                harnessOptions.LoopEvaluators = [new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions
                {
                    Modes = planning.LoopModes ?? ["execute"]
                })];
                // Hardcoded to 5 (not configurable) - see PlanningDefinition remarks.
                harnessOptions.LoopAgentOptions = new LoopAgentOptions { MaxIterations = 5 };
#pragma warning restore MAAI001
            }

#pragma warning disable MAAI001 // MaxContextWindowTokens/MaxOutputTokens are experimental in Microsoft.Agents.AI.Harness.
            harnessOptions.MaxContextWindowTokens = harnessDef.MaxContextWindowTokens;
            harnessOptions.MaxOutputTokens = harnessDef.MaxOutputTokens;
            return chatClient.AsHarnessAgent(harnessOptions);
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
