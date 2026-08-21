using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Application.UnitTests.TestHelpers;
using OpenAgentOrchestrator.Command.Application.Checkpointing;

namespace OpenAgentOrchestrator.Application.UnitTests.Checkpointing
{
    [TestClass]
    public sealed class WorkflowCheckpointStoreTests
    {
        [TestMethod]
        public async Task SaveAsync_ThenLoadAsync_RoundTripsCheckpointDocument()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("checkpoint-store");
            var sut = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var document = new SessionCheckpointDocument
            {
                SessionId = "session-1",
                OrchestratorId = "orch",
                Pattern = "sequential",
                Input = "hello",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                Status = "pending_approval",
                PendingRequestPortId = "port-1",
                Steps =
                [
                    new StepCheckpointRecord
                    {
                        StepIndex = 0,
                        AgentName = "planner",
                        Status = "completed",
                        Output = "draft",
                        DurationMs = 12.5,
                        RecordedAt = DateTime.UtcNow
                    }
                ]
            };

            // Act
            await sut.SaveAsync(document);
            var reloaded = await sut.LoadAsync(document.SessionId);

            // Assert
            reloaded.Should().NotBeNull();
            reloaded!.Status.Should().Be("pending_approval");
            reloaded.Steps.Should().ContainSingle();
            reloaded.Steps[0].AgentName.Should().Be("planner");
            File.Exists(Path.Combine(artifactDirectory.Path, "session-1.json")).Should().BeTrue();
            File.Exists(Path.Combine(artifactDirectory.Path, "session-1.json.tmp")).Should().BeFalse();
        }

        [TestMethod]
        public async Task LoadAsync_WhenFileIsMissing_ReturnsNull()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("checkpoint-store-missing");
            var sut = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);

            // Act
            var result = await sut.LoadAsync("missing");

            // Assert
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task LoadAsync_WhenFileIsBlank_ReturnsNull()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("checkpoint-store-blank");
            File.WriteAllText(Path.Combine(artifactDirectory.Path, "blank.json"), string.Empty);
            var sut = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);

            // Act
            var result = await sut.LoadAsync("blank");

            // Assert
            result.Should().BeNull();
        }
    }
}
