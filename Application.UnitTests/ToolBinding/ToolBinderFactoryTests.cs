using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.ToolBinding
{
    [TestClass]
    public sealed class ToolBinderFactoryTests
    {
        [TestMethod]
        public async Task BindToolsAsync_BindsToolsFromMatchingBinder()
        {
            // Arrange
            var tool = AIFunctionFactory.Create(
                (Func<string>)(() => "ok"),
                name: "lookup",
                description: "Lookup tool",
                serializerOptions: null);

            var binder = new Mock<IToolBinder>();
            binder.SetupGet(instance => instance.SupportedType).Returns("mcp");
            binder.Setup(instance => instance.BindAsync(It.IsAny<ToolDefinition>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([tool]);

            var sut = new ToolBinderFactory([binder.Object]);

            // Act
            var result = await sut.BindToolsAsync(
            [
                new ToolDefinition { Type = "mcp", Name = "lookup", Endpoint = "https://example.test" }
            ]);

            // Assert
            result.Should().ContainSingle()
                .Which.Name.Should().Be("lookup");
        }

        [TestMethod]
        public async Task BindToolsAsync_WhenBinderIsMissing_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new ToolBinderFactory([]);

            // Act
            var action = () => sut.BindToolsAsync(
            [
                new ToolDefinition { Type = "mcp", Name = "lookup", Endpoint = "https://example.test" }
            ]);

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }
    }
}
