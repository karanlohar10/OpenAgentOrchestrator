using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Query.Domain.Model;

namespace OpenAgentOrchestrator.Application.UnitTests.Query
{
    [TestClass]
    public sealed class QueryDomainModelTests
    {
        [TestMethod]
        public void OrchestratorSummaryResponse_PreservesAssignedValues()
        {
            var response = new OrchestratorSummaryResponse
            {
                Id = "orch",
                Name = "Orchestrator",
                Description = "Description",
                Pattern = "sequential",
                AgentCount = 2
            };

            response.Description.Should().Be("Description");
            response.AgentCount.Should().Be(2);
        }

        [TestMethod]
        public void SessionStatusResponse_PreservesAssignedValues()
        {
            var response = new SessionStatusResponse
            {
                SessionId = "session-1",
                OrchestratorId = "orch",
                Status = "pending_approval",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                CompletedAt = null,
                PendingApprovalPrompt = "Approve",
                PendingStepIndex = 1,
                PendingAgentName = "reviewer",
                PendingOutput = "draft"
            };

            response.PendingStepIndex.Should().Be(1);
            response.PendingAgentName.Should().Be("reviewer");
            response.PendingOutput.Should().Be("draft");
        }

        [TestMethod]
        public void SessionCheckpointsResponse_DefaultsAndStepRecordPreserveAssignedValues()
        {
            var response = new SessionCheckpointsResponse
            {
                SessionId = "session-1",
                OrchestratorId = "orch",
                Pattern = "sequential",
                Input = "hello",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow,
                FinalOutput = "world",
                Error = null,
                PendingAgentName = "reviewer",
                PendingStepIndex = 0,
                PendingOutput = "draft",
                PendingApprovalPrompt = "Approve",
                Steps =
                [
                    new StepCheckpointRecordResponse
                    {
                        StepIndex = 0,
                        AgentName = "reviewer",
                        Status = "completed",
                        Output = "draft",
                        DurationMs = 10.5,
                        RecordedAt = DateTime.UtcNow,
                        Edited = true
                    }
                ]
            };

            response.Status.Should().Be("running");
            response.Steps.Should().ContainSingle();
            response.Steps[0].Edited.Should().BeTrue();
            response.Steps[0].StepIndex.Should().Be(0, "callers target a rewind to this step via ResumeRequest.StepIndex");
            response.PendingApprovalPrompt.Should().Be("Approve");
        }
    }
}
