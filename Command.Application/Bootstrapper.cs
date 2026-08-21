namespace OpenAgentOrchestrator.Command.Application
{
    using Microsoft.Agents.AI.Workflows.Checkpointing;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using OpenAgentOrchestrator.Command.Application.Agents;
    using OpenAgentOrchestrator.Command.Application.Checkpointing;
    using OpenAgentOrchestrator.Command.Application.Configuration;
    using OpenAgentOrchestrator.Command.Application.Engine;
    using OpenAgentOrchestrator.Command.Application.Sessions;
    using OpenAgentOrchestrator.Command.Application.ToolBinding;
    using OpenAgentOrchestrator.Command.Application.Tools;
    using OpenAgentOrchestrator.Command.Application.Tools.WebSearch;

    /// <summary>
    /// Registers Command-side application services for orchestrator workflows: file-based
    /// <c>config.yaml</c> config, in-memory session store, file-based checkpoint stores (both the
    /// step-level manifest and Microsoft Agent Framework's own graph-level checkpoints), tool
    /// binding, agent creation (including Agent Harness and Shell Tools support), and the
    /// sequential workflow engine.
    /// </summary>
    public static class CommandApplicationServiceCollectionExtensions
    {
        public static IServiceCollection AddCommandApplication(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<ConfigYamlOptions>(configuration.GetSection("ConfigYaml"));
            services.Configure<AgentDefaults>(configuration.GetSection("AgentDefaults"));
            services.Configure<ObservabilityOptions>(configuration.GetSection("Observability"));

            services.TryAddSingleton<IConfigValidator, ConfigValidator>();
            services.TryAddSingleton<IConfigMerge, ConfigMerge>();
            services.TryAddSingleton<IConfigStore, ConfigStore>();

            services.TryAddSingleton<ISessionStore, InMemorySessionStore>();

            // Step-level checkpoint manifest: one JSON file per session under
            // Checkpointing:RootDirectory (default "checkpoints"), gitignored.
            services.TryAddSingleton<IWorkflowCheckpointStore>(sp =>
            {
                var rootDirectory = configuration["Checkpointing:RootDirectory"] ?? "checkpoints";
                return new JsonFileWorkflowCheckpointStore(rootDirectory);
            });

            // Microsoft Agent Framework's own graph-level checkpoint store (built-in, ships with
            // Microsoft.Agents.AI.Workflows) - one JSON file per workflow run under
            // Checkpointing:MafRootDirectory (default "checkpoints/maf"), gitignored.
            services.TryAddSingleton<JsonCheckpointStore>(sp =>
            {
                var rootDirectory = configuration["Checkpointing:MafRootDirectory"] ?? "checkpoints/maf";
                Directory.CreateDirectory(rootDirectory);
                return new FileSystemJsonCheckpointStore(new DirectoryInfo(rootDirectory));
            });

            services.TryAddSingleton<IWorkflowEngine, WorkflowEngine>();

            // Singleton: runs execute/resume in their own DI scope and cancellation lifetime,
            // decoupled from the calling HTTP request - see WorkflowExecutionCoordinator for why.
            services.TryAddSingleton<IWorkflowExecutionCoordinator, WorkflowExecutionCoordinator>();

            services.TryAddSingleton<ITokenService, ClientCredentialsTokenService>();
            services.TryAddSingleton<IToolBinder, McpToolBinder>();
            services.TryAddSingleton<IToolBinderFactory, ToolBinderFactory>();

            services.TryAddSingleton<IChatClientFactory, ChatClientFactory>();
            services.TryAddSingleton<IShellToolFactory, ShellToolFactory>();

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebSearchProvider, TavilySearchProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebSearchProvider, BingSearchProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebSearchProvider, GoogleSearchProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebSearchProvider, SerpApiSearchProvider>());
            services.TryAddSingleton<IWebSearchToolFactory, WebSearchToolFactory>();

            services.TryAddSingleton<IAgentFactory, AgentFactory>();

            return services;
        }
    }
}
