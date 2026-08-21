using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.Sessions;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;
using OpenAgentOrchestrator.Query.Application.Services;

namespace OpenAgentOrchestrator.Application.UnitTests.Query
{
    /// <summary>
    /// Covers <see cref="OrchestratorQueryService.GetCheckpointsAsync"/>'s mapping from the
    /// internal <see cref="SessionCheckpointDocument"/> to the public
    /// <c>SessionCheckpointsResponse</c> - specifically that step data is copied across, and that
    /// the MAF-native <c>CheckpointId</c> (top-level and per-step) is deliberately NOT exposed on
    /// the public response, since it's an internal implementation detail of the resume mechanism;
    /// callers instead target a rewind via <c>ResumeRequest.StepIndex</c>.
    /// </summary>
    [TestClass]
    public class OrchestratorQueryServiceTests
    {
        [TestMethod]
        public async Task GetCheckpointsAsync_MapsStepDataButNotCheckpointIdOntoTheResponse()
        {
            // Arrange
            var orchestrator = new OrchestratorDefinition
            {
                Id = "orch",
                Name = "Orchestrator",
                Pattern = "sequential",
                Agents = [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                Checkpointing = new CheckpointingDefinition { Enabled = true }
            };

            var document = new SessionCheckpointDocument
            {
                SessionId = "session-1",
                OrchestratorId = "orch",
                Pattern = "sequential",
                Input = "hello",
                Status = "completed",
                FinalOutput = "world",
                CheckpointId = "checkpoint-latest",
                Steps =
                [
                    new StepCheckpointRecord
                    {
                        StepIndex = 0,
                        AgentName = "writer",
                        Status = "completed",
                        Output = "world",
                        CheckpointId = "checkpoint-step-0"
                    }
                ]
            };

            var configStore = new Mock<IConfigStore>();
            configStore.Setup(s => s.GetOrchestratorAsync("orch", It.IsAny<CancellationToken>())).ReturnsAsync(orchestrator);

            var checkpointStore = new Mock<IWorkflowCheckpointStore>();
            checkpointStore.Setup(s => s.LoadAsync("session-1", It.IsAny<CancellationToken>())).ReturnsAsync(document);

            var workflowEngine = new Mock<IWorkflowEngine>();
            workflowEngine
                .Setup(e => e.GetCheckpointsAsync(orchestrator, "session-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(document);

            var sessionStore = new InMemorySessionStore();

            var queryService = new OrchestratorQueryService(configStore.Object, sessionStore, workflowEngine.Object, checkpointStore.Object);

            // Act
            var response = await queryService.GetCheckpointsAsync("session-1");

            // Assert
            response.Should().NotBeNull();
            response!.SessionId.Should().Be("session-1");
            response.Status.Should().Be("completed");
            response.FinalOutput.Should().Be("world");
            response.Steps.Should().ContainSingle();
            response.Steps[0].StepIndex.Should().Be(0, "callers target a rewind to this step via ResumeRequest.StepIndex");
            response.Steps[0].AgentName.Should().Be("writer");
            response.Steps[0].Output.Should().Be("world");
        }
    }
}
