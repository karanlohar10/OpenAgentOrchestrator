using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.Tools;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.ToolBinding
{
    [TestClass]
    public sealed class ShellToolBinderTests
    {
        [TestMethod]
        public void SupportedType_ReturnsShell()
        {
            // Arrange
            var sut = new ShellToolBinder(Mock.Of<IShellToolFactory>());

            // Act
            var result = sut.SupportedType;

            // Assert
            result.Should().Be("shell");
        }

        [TestMethod]
        public async Task BindAsync_DelegatesToShellToolFactoryAndReturnsToolAndContextProvider()
        {
            // Arrange
            var tool = AIFunctionFactory.Create(
                (Func<string>)(() => "ok"),
                name: "shell_execute",
                description: "Executes a shell command",
                serializerOptions: null);
            var contextProvider = new FakeContextProvider();
            var executor = Mock.Of<IAsyncDisposable>();

            var definition = new ToolDefinition
            {
                Type = "shell",
                Name = "local-shell",
                Mode = "persistent",
                AcknowledgeUnsafe = true,
                RequireApproval = false
            };

            var shellToolFactory = new Mock<IShellToolFactory>();
            shellToolFactory
                .Setup(factory => factory.Create(definition))
                .Returns(new ShellToolBinding(tool, contextProvider, executor));

            var sut = new ShellToolBinder(shellToolFactory.Object);

            // Act
            var result = await sut.BindAsync(definition);

            // Assert
            result.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
            result.ContextProviders.Should().ContainSingle().Which.Should().BeSameAs(contextProvider);
            shellToolFactory.Verify(factory => factory.Create(definition), Times.Once);
        }

        [TestMethod]
        public async Task BindAsync_WhenFactoryThrowsBecauseUnacknowledged_Propagates()
        {
            // Arrange - mirrors ShellToolFactory's own guard: acknowledgeUnsafe: false must fail.
            var definition = new ToolDefinition { Type = "shell", Name = "local-shell", AcknowledgeUnsafe = false };

            var shellToolFactory = new Mock<IShellToolFactory>();
            shellToolFactory
                .Setup(factory => factory.Create(definition))
                .Throws(new InvalidOperationException("acknowledgeUnsafe required"));

            var sut = new ShellToolBinder(shellToolFactory.Object);

            // Act
            var action = () => sut.BindAsync(definition);

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        // AIContextProvider is abstract with no accessible parameterless constructor and cannot be
        // mocked by Moq (Castle proxy requires a matching constructor); a trivial concrete
        // subclass is the simplest way to obtain an instance for equality checks in tests.
        private sealed class FakeContextProvider() : AIContextProvider(null, null, null)
        {
        }
    }
}
