using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Tools.WebSearch;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Tools.WebSearch
{
    [TestClass]
    public sealed class WebSearchToolFactoryTests
    {
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();

        [TestMethod]
        public void Create_WhenProviderIsUnknown_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new WebSearchToolFactory([new TavilySearchProvider(_httpClientFactoryMock.Object)]);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "unknown-provider", ApiKey = "key" };

            // Act & Assert
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => sut.Create(definition));
            StringAssert.Contains(exception.Message, "unknown-provider");
        }

        [TestMethod]
        public void Create_ReturnsAIFunctionNamedWebSearch()
        {
            // Arrange
            var sut = new WebSearchToolFactory([new TavilySearchProvider(_httpClientFactoryMock.Object)]);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "key" };

            // Act
            var tool = sut.Create(definition);

            // Assert
            Assert.IsInstanceOfType<AIFunction>(tool);
            Assert.AreEqual("web_search", tool.Name);
        }

        [TestMethod]
        public async Task TavilySearchProvider_ParsesResultsFromResponse()
        {
            // Arrange
            var responseJson = JsonSerializer.Serialize(new
            {
                results = new[]
                {
                    new { title = "Result 1", url = "https://example.test/1", content = "Snippet 1" },
                    new { title = "Result 2", url = "https://example.test/2", content = "Snippet 2" }
                }
            });
            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var provider = new TavilySearchProvider(_httpClientFactoryMock.Object);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "tavily-key", MaxResults = 5 };

            // Act
            var results = await provider.SearchAsync(definition, "test query", default);

            // Assert
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("Result 1", results[0].Title);
            Assert.AreEqual("https://example.test/1", results[0].Url);
            Assert.AreEqual("Snippet 1", results[0].Snippet);
        }

        [TestMethod]
        public async Task TavilySearchProvider_WhenRequestFails_ThrowsInvalidOperationException()
        {
            // Arrange
            var handler = CreateMockHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid api key\"}");
            SetupHttpClientFactory(handler);

            var provider = new TavilySearchProvider(_httpClientFactoryMock.Object);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "bad-key" };

            // Act & Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => provider.SearchAsync(definition, "test query", default));
        }

        [TestMethod]
        public async Task BingSearchProvider_ParsesResultsFromResponse()
        {
            // Arrange
            var responseJson = JsonSerializer.Serialize(new
            {
                webPages = new
                {
                    value = new[]
                    {
                        new { name = "Bing Result", url = "https://example.test/bing", snippet = "Bing snippet" }
                    }
                }
            });
            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var provider = new BingSearchProvider(_httpClientFactoryMock.Object);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "bing", ApiKey = "bing-key" };

            // Act
            var results = await provider.SearchAsync(definition, "test query", default);

            // Assert
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("Bing Result", results[0].Title);
            Assert.AreEqual("https://example.test/bing", results[0].Url);
            Assert.AreEqual("Bing snippet", results[0].Snippet);
        }

        [TestMethod]
        public async Task GoogleSearchProvider_ParsesResultsFromResponse()
        {
            // Arrange
            var responseJson = JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new { title = "Google Result", link = "https://example.test/google", snippet = "Google snippet" }
                }
            });
            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var provider = new GoogleSearchProvider(_httpClientFactoryMock.Object);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "google", ApiKey = "google-key", SearchEngineId = "cx-id" };

            // Act
            var results = await provider.SearchAsync(definition, "test query", default);

            // Assert
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("Google Result", results[0].Title);
            Assert.AreEqual("https://example.test/google", results[0].Url);
            Assert.AreEqual("Google snippet", results[0].Snippet);
        }

        [TestMethod]
        public async Task SerpApiSearchProvider_ParsesResultsFromResponse()
        {
            // Arrange
            var responseJson = JsonSerializer.Serialize(new
            {
                organic_results = new[]
                {
                    new { title = "SerpApi Result", link = "https://example.test/serpapi", snippet = "SerpApi snippet" }
                }
            });
            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var provider = new SerpApiSearchProvider(_httpClientFactoryMock.Object);
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "serpapi", ApiKey = "serpapi-key" };

            // Act
            var results = await provider.SearchAsync(definition, "test query", default);

            // Assert
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("SerpApi Result", results[0].Title);
            Assert.AreEqual("https://example.test/serpapi", results[0].Url);
            Assert.AreEqual("SerpApi snippet", results[0].Snippet);
        }

        private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string content)
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = statusCode,
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            return handler;
        }

        private void SetupHttpClientFactory(Mock<HttpMessageHandler> handler)
        {
            var httpClient = new HttpClient(handler.Object);
            _httpClientFactoryMock
                .Setup(x => x.CreateClient("WebSearchTool"))
                .Returns(httpClient);
        }
    }
}
