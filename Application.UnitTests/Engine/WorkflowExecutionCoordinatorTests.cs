using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    /// <summary>
    /// Verifies <see cref="WorkflowExecutionCoordinator"/> runs each execute/resume call in its
    /// own DI scope and against <see cref="IHostApplicationLifetime.ApplicationStopping"/> -
    /// never the caller's own token - which is what prevents a disconnected HTTP client from
    /// interrupting/mis-finalizing an in-progress workflow (see the type's own remarks for the
    /// full rationale).
    /// </summary>
    [TestClass]
    public sealed class WorkflowExecutionCoordinatorTests
    {
        private static (WorkflowExecutionCoordinator Coordinator, Mock<IWorkflowEngine> EngineMock, Mock<IServiceScope> ScopeMock, CancellationToken StoppingToken)
            CreateCoordinator()
        {
            var engineMock = new Mock<IWorkflowEngine>();

            var serviceProviderMock = new Mock<IServiceProvider>();
            serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IWorkflowEngine)))
                .Returns(engineMock.Object);

            var scopeMock = new Mock<IServiceScope>();
            scopeMock.SetupGet(scope => scope.ServiceProvider).Returns(serviceProviderMock.Object);

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(factory => factory.CreateScope()).Returns(scopeMock.Object);

            using var stoppingCts = new CancellationTokenSource();
            var stoppingToken = stoppingCts.Token;

            var appLifetimeMock = new Mock<IHostApplicationLifetime>();
            appLifetimeMock.SetupGet(lifetime => lifetime.ApplicationStopping).Returns(stoppingToken);

            var coordinator = new WorkflowExecutionCoordinator(scopeFactoryMock.Object, appLifetimeMock.Object);

            return (coordinator, engineMock, scopeMock, stoppingToken);
        }

        [TestMethod]
        public async Task ExecuteAsync_CreatesOwnScopeAndUsesApplicationStoppingToken_NotACallerToken()
        {
            // Arrange
            var (coordinator, engineMock, scopeMock, stoppingToken) = CreateCoordinator();
            var orchestrator = new OrchestratorDefinition { Id = "orch", Name = "Orchestrator", Pattern = "sequential", Agents = [], Checkpointing = new CheckpointingDefinition() };
            var request = new ExecuteRequest { Input = "input" };
            var expectedResponse = new ExecuteResponse { SessionId = "session-1", Status = "completed", Steps = [] };

            engineMock
                .Setup(engine => engine.ExecuteAsync(orchestrator, request, stoppingToken))
                .ReturnsAsync(expectedResponse);

            // Act
            var response = await coordinator.ExecuteAsync(orchestrator, request);

            // Assert
            response.Should().BeSameAs(expectedResponse);
            engineMock.Verify(engine => engine.ExecuteAsync(orchestrator, request, stoppingToken), Times.Once);
            scopeMock.Verify(scope => scope.Dispose(), Times.Once);
        }

        [TestMethod]
        public async Task ResumeAsync_CreatesOwnScopeAndUsesApplicationStoppingToken_NotACallerToken()
        {
            // Arrange
            var (coordinator, engineMock, scopeMock, stoppingToken) = CreateCoordinator();
            var orchestrator = new OrchestratorDefinition { Id = "orch", Name = "Orchestrator", Pattern = "sequential", Agents = [], Checkpointing = new CheckpointingDefinition() };
            var request = new ResumeRequest { Action = ResumeAction.Continue };
            var expectedResponse = new ExecuteResponse { SessionId = "session-1", Status = "completed", Steps = [] };

            engineMock
                .Setup(engine => engine.ResumeAsync(orchestrator, "session-1", request, stoppingToken))
                .ReturnsAsync(expectedResponse);

            // Act
            var response = await coordinator.ResumeAsync(orchestrator, "session-1", request);

            // Assert
            response.Should().BeSameAs(expectedResponse);
            engineMock.Verify(engine => engine.ResumeAsync(orchestrator, "session-1", request, stoppingToken), Times.Once);
            scopeMock.Verify(scope => scope.Dispose(), Times.Once);
        }
    }
}
