using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    /// <summary>
    /// Calls Tavily's search API (https://docs.tavily.com/documentation/api-reference/endpoint/search),
    /// purpose-built for LLM/agent search consumption. Chosen as this project's default provider
    /// for its simple REST API and free tier.
    /// </summary>
    public sealed class TavilySearchProvider : IWebSearchProvider
    {
        private const string Endpoint = "https://api.tavily.com/search";
        private readonly IHttpClientFactory _httpClientFactory;

        public string ProviderName => "tavily";

        public TavilySearchProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            WebSearchToolDefinition definition, string query, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("WebSearchTool");

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Headers = { { "Authorization", $"Bearer {definition.ApiKey}" } },
                Content = JsonContent.Create(new TavilyRequest
                {
                    Query = query,
                    MaxResults = definition.MaxResults,
                    SearchDepth = definition.SearchDepth
                })
            };

            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Tavily search request failed with {response.StatusCode}: {errorBody}");
            }

            var body = await response.Content.ReadFromJsonAsync<TavilyResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Tavily search returned an empty response.");

            return (body.Results ?? [])
                .Select(r => new WebSearchResult(r.Title ?? string.Empty, r.Url ?? string.Empty, r.Content ?? string.Empty))
                .ToList();
        }

        private sealed class TavilyRequest
        {
            [JsonPropertyName("query")]
            public required string Query { get; set; }

            [JsonPropertyName("max_results")]
            public int MaxResults { get; set; }

            [JsonPropertyName("search_depth")]
            public string? SearchDepth { get; set; }
        }

        private sealed class TavilyResponse
        {
            [JsonPropertyName("results")]
            public List<TavilyResult>? Results { get; set; }
        }

        private sealed class TavilyResult
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}
