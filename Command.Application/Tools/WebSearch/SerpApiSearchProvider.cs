using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    /// <summary>
    /// Calls SerpApi's search endpoint (https://serpapi.com/search-api), which proxies a chosen
    /// underlying search engine (see <see cref="ToolDefinition.SearchEngine"/>).
    /// </summary>
    public sealed class SerpApiSearchProvider : IWebSearchProvider
    {
        private const string Endpoint = "https://serpapi.com/search.json";
        private readonly IHttpClientFactory _httpClientFactory;

        public string ProviderName => "serpapi";

        public SerpApiSearchProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            ToolDefinition definition, string query, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("WebSearchTool");

            var url = $"{Endpoint}?engine={Uri.EscapeDataString(definition.SearchEngine)}" +
                      $"&q={Uri.EscapeDataString(query)}&api_key={Uri.EscapeDataString(definition.ApiKey)}" +
                      $"&num={definition.MaxResults}";

            using var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"SerpApi search request failed with {response.StatusCode}: {errorBody}");
            }

            var body = await response.Content.ReadFromJsonAsync<SerpApiResponse>(cancellationToken)
                ?? throw new InvalidOperationException("SerpApi search returned an empty response.");

            return (body.OrganicResults ?? [])
                .Take(definition.MaxResults)
                .Select(r => new WebSearchResult(r.Title ?? string.Empty, r.Link ?? string.Empty, r.Snippet ?? string.Empty))
                .ToList();
        }

        private sealed class SerpApiResponse
        {
            [JsonPropertyName("organic_results")]
            public List<SerpApiResult>? OrganicResults { get; set; }
        }

        private sealed class SerpApiResult
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("link")]
            public string? Link { get; set; }

            [JsonPropertyName("snippet")]
            public string? Snippet { get; set; }
        }
    }
}
