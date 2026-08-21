using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    /// <summary>
    /// A single normalized web-search result, shape shared across all
    /// <see cref="IWebSearchProvider"/> implementations regardless of the underlying REST API's
    /// own response format.
    /// </summary>
    public sealed record WebSearchResult(string Title, string Url, string Snippet);

    /// <summary>
    /// Calls a specific third-party web-search REST API and normalizes its response. One
    /// implementation per <see cref="WebSearchToolDefinition.Provider"/> value.
    /// </summary>
    public interface IWebSearchProvider
    {
        /// <summary>The <see cref="WebSearchToolDefinition.Provider"/> value this implementation handles.</summary>
        string ProviderName { get; }

        Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            WebSearchToolDefinition definition, string query, CancellationToken cancellationToken);
    }
}
