using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Contracts
{
    [TestClass]
    public sealed class ContractAndConfigurationDefaultsTests
    {
        [TestMethod]
        public void ResumeRequest_DefaultsActionToContinueAndEditedOutputToNull()
        {
            // Arrange
            var request = new ResumeRequest();

            // Assert
            request.Action.Should().Be(ResumeAction.Continue);
            request.EditedOutput.Should().BeNull();
            request.StepIndex.Should().BeNull();
        }

        [TestMethod]
        public void ExecuteRequestAndExecuteResponse_PreserveAssignedValues()
        {
            // Arrange
            var request = new ExecuteRequest
            {
                Input = "hello",
                SessionId = "session-1",
                Context = new Dictionary<string, string> { ["tenant"] = "test" }
            };
            var response = new ExecuteResponse
            {
                SessionId = "session-1",
                Status = "completed",
                Output = "world",
                Error = null,
                Steps =
                [
                    new AgentStepResult
                    {
                        AgentName = "planner",
                        Status = "completed",
                        Output = "world",
                        DurationMs = 1.5
                    }
                ]
            };

            // Assert
            request.Context.Should().ContainKey("tenant").WhoseValue.Should().Be("test");
            response.Steps.Should().ContainSingle();
            response.Steps![0].DurationMs.Should().Be(1.5);
        }

        [TestMethod]
        public void SessionCheckpointDocument_DefaultsStatusAndStepCollection()
        {
            // Arrange
            var document = new SessionCheckpointDocument
            {
                SessionId = "session-1",
                OrchestratorId = "orch",
                Pattern = "sequential",
                Input = "hello"
            };

            // Assert
            document.Status.Should().Be("running");
            document.Steps.Should().BeEmpty();
        }

        [TestMethod]
        public void AgentDefinitionAndProviderDefinition_PreserveAssignedValues()
        {
            // Arrange
            var agent = new AgentDefinition
            {
                Name = "planner",
                Instructions = "Plan.",
                Provider = "azure",
                Model = "gpt-4o-mini",
                Tools = [new ToolDefinition
                {
                    Type = "mcp",
                    Name = "lookup",
                    Endpoint = "https://example.test",
                    Headers = new Dictionary<string, string> { ["Authorization"] = "******" }
                }],
                ResponseFormat = new ResponseFormatDefinition { Type = "text" }
            };
            var provider = new ProviderDefinition
            {
                Id = "azure",
                Type = "azure-openai",
                Endpoint = "https://example.test",
                ApiKey = "key"
            };
            var checkpointing = new CheckpointingDefinition
            {
                Enabled = true
            };

            // Assert
            agent.Tools.Should().ContainSingle();
            agent.Tools![0].Headers.Should().ContainKey("Authorization");
            agent.ResponseFormat!.Type.Should().Be("text");
            provider.ApiKey.Should().Be("key");
            checkpointing.Enabled.Should().BeTrue();
        }
    }
}