using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools.WebSearch
{
    /// <summary>
    /// Calls the Google Custom Search JSON API (https://developers.google.com/custom-search/v1/reference/rest/v1/cse/list).
    /// Requires <see cref="WebSearchToolDefinition.SearchEngineId"/> (the "cx" Programmable
    /// Search Engine id) in addition to the API key.
    /// </summary>
    public sealed class GoogleSearchProvider : IWebSearchProvider
    {
        private const string Endpoint = "https://www.googleapis.com/customsearch/v1";
        private readonly IHttpClientFactory _httpClientFactory;

        public string ProviderName => "google";

        public GoogleSearchProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
            WebSearchToolDefinition definition, string query, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient("WebSearchTool");

            // Google Custom Search caps results per request at 10 ("num" parameter).
            var num = Math.Clamp(definition.MaxResults, 1, 10);
            var url = $"{Endpoint}?key={Uri.EscapeDataString(definition.ApiKey)}" +
                      $"&cx={Uri.EscapeDataString(definition.SearchEngineId ?? string.Empty)}" +
                      $"&q={Uri.EscapeDataString(query)}&num={num}";

            using var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Google Custom Search request failed with {response.StatusCode}: {errorBody}");
            }

            var body = await response.Content.ReadFromJsonAsync<GoogleResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Google Custom Search returned an empty response.");

            return (body.Items ?? [])
                .Select(r => new WebSearchResult(r.Title ?? string.Empty, r.Link ?? string.Empty, r.Snippet ?? string.Empty))
                .ToList();
        }

        private sealed class GoogleResponse
        {
            [JsonPropertyName("items")]
            public List<GoogleResult>? Items { get; set; }
        }

        private sealed class GoogleResult
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
