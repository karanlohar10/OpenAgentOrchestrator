using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Moq.Protected;
using OpenAgentOrchestrator.Command.Application.ToolBinding;

namespace OpenAgentOrchestrator.Application.UnitTests.ToolBinding
{
    [TestClass]
    public sealed class ClientCredentialsTokenServiceTests
    {
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
        private readonly Mock<ILogger<ClientCredentialsTokenService>> _loggerMock = new();

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenTokenEndpointIsEmpty_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act & Assert
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => sut.GetAccessTokenAsync("", "client", "secret"));
        }

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenClientIdIsEmpty_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act & Assert
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => sut.GetAccessTokenAsync("https://login.example.com/token", "", "secret"));
        }

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenClientSecretIsEmpty_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act & Assert
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => sut.GetAccessTokenAsync("https://login.example.com/token", "client", ""));
        }

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenTokenEndpointReturnsSuccess_ReturnsAccessToken()
        {
            // Arrange
            var expectedToken = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.test-token";
            var responseJson = JsonSerializer.Serialize(new
            {
                access_token = expectedToken,
                expires_in = 3600,
                token_type = "Bearer"
            });

            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var sut = CreateSut();

            // Act
            var token = await sut.GetAccessTokenAsync(
                "https://login.example.com/token", "client-id", "client-secret", "api://test/.default");

            // Assert
            Assert.AreEqual(expectedToken, token);
        }

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenCalledTwice_ReturnsCachedToken()
        {
            // Arrange
            var expectedToken = "cached-token";
            var responseJson = JsonSerializer.Serialize(new
            {
                access_token = expectedToken,
                expires_in = 3600,
                token_type = "Bearer"
            });

            var handler = CreateMockHandler(HttpStatusCode.OK, responseJson);
            SetupHttpClientFactory(handler);

            var sut = CreateSut();

            // Act
            var token1 = await sut.GetAccessTokenAsync(
                "https://login.example.com/token", "client-id", "client-secret");
            var token2 = await sut.GetAccessTokenAsync(
                "https://login.example.com/token", "client-id", "client-secret");

            // Assert
            Assert.AreEqual(token1, token2);
            handler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [TestMethod]
        public async Task GetAccessTokenAsync_WhenTokenEndpointReturnsError_ThrowsInvalidOperationException()
        {
            // Arrange
            var handler = CreateMockHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}");
            SetupHttpClientFactory(handler);

            var sut = CreateSut();

            // Act & Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => sut.GetAccessTokenAsync(
                    "https://login.example.com/token", "client-id", "wrong-secret"));
        }

        private ClientCredentialsTokenService CreateSut() =>
            new(_httpClientFactoryMock.Object, _loggerMock.Object);

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
                    Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
                });
            return handler;
        }

        private void SetupHttpClientFactory(Mock<HttpMessageHandler> handler)
        {
            var httpClient = new HttpClient(handler.Object);
            _httpClientFactoryMock
                .Setup(x => x.CreateClient("TokenService"))
                .Returns(httpClient);
        }
    }
}
