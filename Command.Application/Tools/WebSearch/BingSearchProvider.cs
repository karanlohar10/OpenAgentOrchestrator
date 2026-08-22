using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    /// <summary>
    /// Calls the Bing Web Search API v7 (https://learn.microsoft.com/bing/search-apis/bing-web-search/reference/endpoints).
    /// </summary>
    public sealed class BingSearchProvider : IWebSearchProvider
    {
        private const string Endpoint = "https://api.bing.microsoft.com/v7.0/search";
        private readonly IHttpClientFactory _httpClientFactory;

        public string ProviderName => "bing";

        public BingSearchProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            ToolDefinition definition, string query, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("WebSearchTool");

            var url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&count={definition.MaxResults}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { { "Ocp-Apim-Subscription-Key", definition.ApiKey } }
            };

            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Bing search request failed with {response.StatusCode}: {errorBody}");
            }

            var body = await response.Content.ReadFromJsonAsync<BingResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Bing search returned an empty response.");

            return (body.WebPages?.Value ?? [])
                .Select(r => new WebSearchResult(r.Name ?? string.Empty, r.Url ?? string.Empty, r.Snippet ?? string.Empty))
                .ToList();
        }

        private sealed class BingResponse
        {
            [JsonPropertyName("webPages")]
            public BingWebPages? WebPages { get; set; }
        }

        private sealed class BingWebPages
        {
            [JsonPropertyName("value")]
            public List<BingResult>? Value { get; set; }
        }

        private sealed class BingResult
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("snippet")]
            public string? Snippet { get; set; }
        }
    }
}
