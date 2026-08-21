using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Agents;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Agents
{
    [TestClass]
    public sealed class ChatClientFactoryTests
    {
        [TestMethod]
        public void Create_WhenProviderTypeIsUnsupported_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new ChatClientFactory();
            var provider = new ProviderDefinition { Id = "custom", Type = "custom" };

            // Act
            Action action = () => sut.Create(provider, "model");

            // Assert
            var exception = Assert.ThrowsExactly<InvalidOperationException>(action);
            exception.Message.Should().Contain("Unsupported provider type");
        }

        [TestMethod]
        public void Create_AzureOpenAiProvider_ReturnsChatClient()
        {
            var result = new ChatClientFactory().Create(
                new ProviderDefinition
                {
                    Id = "azure",
                    Type = "azure-openai",
                    Endpoint = "https://example.openai.azure.com",
                    ApiKey = "key"
                },
                "gpt-4o-mini");

            result.Should().NotBeNull();
        }

        [TestMethod]
        public void Create_AzureOpenAiProviderWithoutEndpoint_ThrowsArgumentNullException()
        {
            var sut = new ChatClientFactory();

            Action action = () => sut.Create(
                new ProviderDefinition
                {
                    Id = "azure",
                    Type = "azure-openai",
                    ApiKey = "key"
                },
                "gpt-4o-mini");

            Assert.ThrowsExactly<ArgumentNullException>(action);
        }
    }
}
