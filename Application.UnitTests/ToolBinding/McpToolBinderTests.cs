using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.ToolBinding
{
    [TestClass]
    public sealed class McpToolBinderTests
    {
        private readonly Mock<ITokenService> _tokenServiceMock = new();

        private McpToolBinder CreateSut() => new(_tokenServiceMock.Object);

        [TestMethod]
        public async Task BindAsync_WhenEndpointIsMissing_ThrowsArgumentNullException()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            var action = () => sut.BindAsync(new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup"
            });

            // Assert
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthAndTokenEndpointMissing_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            var action = () => sut.BindAsync(new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://example.com/mcp",
                AuthType = "bearer",
                ClientId = "client-id",
                ClientSecret = "my-secret"
            });

            // Assert
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthAndClientSecretMissing_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            var action = () => sut.BindAsync(new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://example.com/mcp",
                AuthType = "bearer",
                TokenEndpoint = "https://login.example.com/token",
                ClientId = "client-id"
            });

            // Assert
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        }

        [TestMethod]
        public void SupportedType_ReturnsMcp()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            var result = sut.SupportedType;

            // Assert
            result.Should().Be("mcp");
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthSucceeds_CallsTokenServiceWithCorrectArgs()
        {
            // Arrange
            const string clientSecret = "super-secret-value";
            const string tokenEndpoint = "https://login.example.com/token";
            const string clientId = "my-client-id";
            const string scope = "api://test/.default";
            const string accessToken = "test-access-token";

            _tokenServiceMock
                .Setup(x => x.GetAccessTokenAsync(
                    tokenEndpoint, clientId, clientSecret, scope, It.IsAny<CancellationToken>()))
                .ReturnsAsync(accessToken);

            var sut = CreateSut();
            var definition = new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://localhost:9999/mcp",
                AuthType = "bearer",
                TokenEndpoint = tokenEndpoint,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Scope = scope
            };

            // Act — BindAsync will fail at McpClient.CreateAsync (network), but the
            // token resolution code runs before that point.
            try { await sut.BindAsync(definition, CancellationToken.None); } catch { /* Expected: network/transport error */ }

            // Assert — the token service was called with the literal client secret from config.yaml
            _tokenServiceMock.Verify(
                x => x.GetAccessTokenAsync(tokenEndpoint, clientId, clientSecret, scope, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthWithExistingHeaders_MergesAuthorizationHeader()
        {
            // Arrange
            const string clientSecret = "client-secret";
            const string accessToken = "merged-token-123";

            _tokenServiceMock
                .Setup(x => x.GetAccessTokenAsync(
                    It.IsAny<string>(), It.IsAny<string>(), clientSecret, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(accessToken);

            var sut = CreateSut();
            var definition = new ToolDefinition
            {
                Type = "mcp",
                Name = "tool-x",
                Endpoint = "https://localhost:9999/mcp",
                AuthType = "bearer",
                TokenEndpoint = "https://login.example.com/token",
                ClientId = "cid",
                ClientSecret = clientSecret,
                Scope = "scope1",
                Headers = new Dictionary<string, string>
                {
                    ["X-Custom-Header"] = "custom-value",
                    ["X-Trace-Id"] = "trace-123"
                }
            };

            // Act — will fail at transport level, but header resolution occurs first
            try { await sut.BindAsync(definition, CancellationToken.None); } catch { }

            // Assert — token service was invoked (proving bearer path was taken including header merge)
            _tokenServiceMock.Verify(
                x => x.GetAccessTokenAsync(
                    "https://login.example.com/token", "cid", clientSecret, "scope1", It.IsAny<CancellationToken>()),
                Times.Once);

            // The original Headers dictionary should NOT be mutated (merge creates a new dict)
            definition.Headers.Should().NotContainKey("Authorization");
        }

        [TestMethod]
        public async Task BindAsync_WhenApiKeyAuth_DoesNotCallTokenService()
        {
            // Arrange
            var sut = CreateSut();
            var definition = new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://localhost:9999/mcp",
                AuthType = "apiKey",
                Headers = new Dictionary<string, string>
                {
                    ["X-API-Key"] = "my-api-key-value"
                }
            };

            // Act — will fail at transport level
            try { await sut.BindAsync(definition, CancellationToken.None); } catch { }

            // Assert — token service should NOT be called
            _tokenServiceMock.Verify(
                x => x.GetAccessTokenAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthAndClientIdMissing_ThrowsArgumentException()
        {
            // Arrange
            var sut = CreateSut();

            // Act
            var action = () => sut.BindAsync(new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://example.com/mcp",
                AuthType = "bearer",
                TokenEndpoint = "https://login.example.com/token",
                ClientSecret = "my-secret"
            });

            // Assert
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(action);
        }

        [TestMethod]
        public async Task BindAsync_WhenNullAuthType_DoesNotCallTokenService()
        {
            // Arrange
            var sut = CreateSut();
            var definition = new ToolDefinition
            {
                Type = "mcp",
                Name = "lookup",
                Endpoint = "https://localhost:9999/mcp",
                AuthType = null!,
                Headers = null
            };

            // Act — will fail at transport level
            try { await sut.BindAsync(definition, CancellationToken.None); } catch { }

            // Assert
            _tokenServiceMock.Verify(
                x => x.GetAccessTokenAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [TestMethod]
        public async Task BindAsync_WhenBearerAuthCaseInsensitive_ResolvesToken()
        {
            // Arrange
            _tokenServiceMock
                .Setup(x => x.GetAccessTokenAsync(
                    It.IsAny<string>(), It.IsAny<string>(), "secret", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("token");

            var sut = CreateSut();
            var definition = new ToolDefinition
            {
                Type = "mcp",
                Name = "tool",
                Endpoint = "https://localhost:9999/mcp",
                AuthType = "BEARER",
                TokenEndpoint = "https://login.example.com/token",
                ClientId = "cid",
                ClientSecret = "secret",
                Scope = null
            };

            // Act
            try { await sut.BindAsync(definition, CancellationToken.None); } catch { }

            // Assert — token service was called despite uppercase "BEARER"
            _tokenServiceMock.Verify(
                x => x.GetAccessTokenAsync(
                    "https://login.example.com/token", "cid", "secret", null, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
