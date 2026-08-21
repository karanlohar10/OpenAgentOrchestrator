using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    public interface IWebSearchToolFactory
    {
        /// <summary>
        /// Builds a <c>web_search</c> <see cref="AITool"/> backed by the provider selected in
        /// <paramref name="definition"/>. Throws if <see cref="WebSearchToolDefinition.Provider"/>
        /// doesn't match a known provider (should already have been caught by config validation
        /// at startup, but this is the last line of defense at agent-creation time).
        /// </summary>
        AITool Create(WebSearchToolDefinition definition);
    }

    /// <summary>
    /// Builds a custom, provider-agnostic web-search <see cref="AITool"/> from
    /// <see cref="WebSearchToolDefinition"/>, dispatching the actual REST call to one of several
    /// <see cref="IWebSearchProvider"/> implementations selected by
    /// <see cref="WebSearchToolDefinition.Provider"/>. Distinct from - and independent of - any
    /// model-provider-hosted web search a Harness agent may attach automatically; see
    /// <see cref="HarnessOptionsDefinition.DisableWebSearch"/>.
    /// </summary>
    public sealed class WebSearchToolFactory : IWebSearchToolFactory
    {
        private readonly Dictionary<string, IWebSearchProvider> _providers;

        public WebSearchToolFactory(IEnumerable<IWebSearchProvider> providers)
        {
            _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
        }

        public AITool Create(WebSearchToolDefinition definition)
        {
            if (!_providers.TryGetValue(definition.Provider, out var provider))
            {
                throw new InvalidOperationException(
                    $"Unknown web search provider '{definition.Provider}'. Must be one of: " +
                    string.Join(", ", _providers.Keys));
            }

            return AIFunctionFactory.Create(
                (string query, CancellationToken cancellationToken) => SearchAsync(provider, definition, query, cancellationToken),
                name: "web_search",
                description: "Searches the web for up-to-date information and returns a list of results " +
                             "(title, url, and a short content snippet) relevant to the query.");
        }

        private static async Task<string> SearchAsync(
            IWebSearchProvider provider, WebSearchToolDefinition definition, string query, CancellationToken cancellationToken)
        {
            var results = await provider.SearchAsync(definition, query, cancellationToken);
            return JsonSerializer.Serialize(results);
        }
    }
}
