using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.Tools.WebSearch;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.ToolBinding
{
    [TestClass]
    public sealed class WebSearchToolBinderTests
    {
        [TestMethod]
        public void SupportedType_ReturnsWebSearch()
        {
            // Arrange
            var sut = new WebSearchToolBinder(Mock.Of<IWebSearchToolFactory>());

            // Act
            var result = sut.SupportedType;

            // Assert
            result.Should().Be("web-search");
        }

        [TestMethod]
        public async Task BindAsync_DelegatesToWebSearchToolFactoryAndReturnsToolWithNoContextProviders()
        {
            // Arrange
            var tool = AIFunctionFactory.Create(
                (Func<string>)(() => "results"),
                name: "web_search",
                description: "Searches the web",
                serializerOptions: null);

            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "test-key" };

            var webSearchToolFactory = new Mock<IWebSearchToolFactory>();
            webSearchToolFactory
                .Setup(factory => factory.Create(definition))
                .Returns(tool);

            var sut = new WebSearchToolBinder(webSearchToolFactory.Object);

            // Act
            var result = await sut.BindAsync(definition);

            // Assert
            result.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
            result.ContextProviders.Should().BeNull();
            webSearchToolFactory.Verify(factory => factory.Create(definition), Times.Once);
        }

        [TestMethod]
        public async Task BindAsync_WhenFactoryThrowsBecauseProviderUnknown_Propagates()
        {
            // Arrange
            var definition = new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "not-a-real-provider", ApiKey = "test-key" };

            var webSearchToolFactory = new Mock<IWebSearchToolFactory>();
            webSearchToolFactory
                .Setup(factory => factory.Create(definition))
                .Throws(new InvalidOperationException("Unknown web search provider"));

            var sut = new WebSearchToolBinder(webSearchToolFactory.Object);

            // Act
            var action = () => sut.BindAsync(definition);

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }
    }
}
