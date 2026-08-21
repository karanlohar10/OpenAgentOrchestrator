namespace OpenAgentOrchestrator.Query.Application
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using OpenAgentOrchestrator.Query.Application.Services;

    /// <summary>
    /// Registers Query-side application services.
    /// </summary>
    public static class QueryApplicationServiceCollectionExtensions
    {
        public static IServiceCollection AddQueryApplication(this IServiceCollection services)
        {
            services.TryAddSingleton<IOrchestratorQueryService, OrchestratorQueryService>();
            return services;
        }
    }
}
