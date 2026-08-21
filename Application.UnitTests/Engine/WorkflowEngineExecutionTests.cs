using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Json;
using OpenAgentOrchestrator.Application.UnitTests.TestHelpers;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.Sessions;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    [TestClass]
    public sealed class WorkflowEngineExecutionTests
    {
        [TestMethod]
        public async Task ExecuteAsync_SequentialWorkflow_CompletesAndStoresStepOutputs()
        {
            // Arrange
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "planner", Instructions = "Plan.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }
                ]);

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["planner"] = WorkflowTestDoubles.CreateAgent("planner", firstClient),
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", secondClient)
                });

            // Act
            var response = await engine.ExecuteAsync(
                orchestrator,
                new ExecuteRequest { Input = "summarize" });

            // Assert
            response.Status.Should().Be("completed");
            response.Output.Should().Be("final:draft:summarize");
            response.Steps.Should().HaveCount(2);
            response.Steps![0].AgentName.Should().Be("planner");
            response.Steps[0].Output.Should().Be("draft:summarize");
            response.Steps[1].AgentName.Should().Be("writer");
            response.Steps[1].Output.Should().Be("final:draft:summarize");

            var session = sessionStore.Get(response.SessionId);
            session.Should().NotBeNull();
            session!.Status.Should().Be("completed");
            session.Output.Should().Be("final:draft:summarize");
            session.CompletedAt.Should().NotBeNull();
        }

        /// <summary>
        /// Regression test for the bug where a run interrupted by a cancelled token (e.g. a
        /// disconnected caller, before the coordinator-based fix decoupled the drain loop's token
        /// from the HTTP request's) got wrongly persisted as "completed". MAF's
        /// StreamingRun.WatchStreamAsync(ct) silently ends its event stream on a cancelled token
        /// without throwing - an already-cancelled token reproduces that deterministically (the
        /// stream ends before yielding any event), exercising ConsumeRunAsync's defensive
        /// ct.ThrowIfCancellationRequested() check after the drain loop.
        /// </summary>
        [TestMethod]
        public async Task ExecuteAsync_TokenCancelledBeforeRunStarts_DoesNotMarkSessionCompleted()
        {
            // Arrange
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "planner", Instructions = "Plan.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }
                ]);

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["planner"] = WorkflowTestDoubles.CreateAgent("planner", firstClient),
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", secondClient)
                });

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var request = new ExecuteRequest { Input = "summarize" };

            // Act
            Func<Task> act = () => engine.ExecuteAsync(orchestrator, request, cts.Token);

            // Assert
            var thrown = await act.Should().ThrowAsync<OrchestratorExecutionException>();
            thrown.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();

            var session = sessionStore.Get(thrown.Which.SessionId);
            session.Should().NotBeNull();
            session!.Status.Should().Be("failed");
            session.Status.Should().NotBe("completed");
        }

        [TestMethod]
        public async Task ExecuteAsync_HumanInLoopWithCheckpointing_PersistsCheckpointAndResumeAsyncRehydratesRun()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-checkpoints");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "publisher", Instructions = "Publish.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition
                {
                    Enabled = true,
                    ApprovalPrompt = "Approve this output."
                },
                checkpointing: new CheckpointingDefinition
                {
                    Enabled = true
                });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", firstClient),
                    ["publisher"] = WorkflowTestDoubles.CreateAgent("publisher", secondClient)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            // Act
            var executeResponse = await engine.ExecuteAsync(
                orchestrator,
                new ExecuteRequest { Input = "input" });
            var checkpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            var firstResumeResponse = await engine.ResumeAsync(
                orchestrator,
                executeResponse.SessionId,
                new ResumeRequest
                {
                    Action = ResumeAction.Continue,
                    EditedOutput = "approved draft"
                });
            var checkpointAfterFirstResume = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            var secondResumeResponse = await engine.ResumeAsync(
                orchestrator,
                executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });
            var completedCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);

            // Assert
            executeResponse.Status.Should().Be("pending_approval");
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("pending_approval");
            checkpoint.PendingRequestPortId.Should().NotBeNullOrEmpty();
            checkpoint.CheckpointId.Should().NotBeNullOrEmpty();
            checkpoint.PendingOutput.Should().Be("draft:input");
            checkpoint.Steps.Should().ContainSingle();

            firstResumeResponse.Status.Should().Be("pending_approval");
            firstResumeResponse.Output.Should().Be("final:approved draft");
            firstResumeResponse.Steps.Should().HaveCount(2);
            firstResumeResponse.Steps![0].Output.Should().Be("approved draft");
            firstResumeResponse.Steps[1].Output.Should().Be("final:approved draft");

            secondResumeResponse.Status.Should().Be("completed");
            secondResumeResponse.Output.Should().Be("final:approved draft");
            secondResumeResponse.Steps.Should().HaveCount(2);

            completedCheckpoint.Should().NotBeNull();
            completedCheckpoint!.Status.Should().Be("completed");
            completedCheckpoint.FinalOutput.Should().Be("final:approved draft");
            completedCheckpoint.Steps.Should().HaveCount(2);

            File.Exists(System.IO.Path.Combine(artifactDirectory.Path, $"{executeResponse.SessionId}.json")).Should().BeTrue();
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenAgentExecutionFails_ThrowsOrchestratorExecutionExceptionAndMarksSessionFailed()
        {
            // Arrange
            var failingClient = new RecordingChatClient((_, _, _) =>
                throw new HttpRequestException("too many requests", null, System.Net.HttpStatusCode.TooManyRequests));

            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", failingClient)
                });

            // Act
            var exception = await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(() =>
                engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }));

            // Assert
            exception.Category.Should().Be(OrchestratorErrorCategory.Unexpected);
            var session = sessionStore.Get(exception.SessionId);
            session.Should().NotBeNull();
            session!.Status.Should().Be("failed");
            session.Error.Should().Be("Workflow for orchestrator 'orch' failed.");
            session.CompletedAt.Should().NotBeNull();
        }

        [TestMethod]
        public async Task ResumeAsync_WhenCheckpointedApprovalIsRejected_UpdatesCheckpointStatus()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-reject-checkpoint");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true },
                checkpointing: new CheckpointingDefinition
                {
                    Enabled = true
                });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", firstClient)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);

            // Act
            var response = await engine.ResumeAsync(
                orchestrator,
                executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Reject });
            var checkpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);

            // Assert
            response.Status.Should().Be("rejected");
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("rejected");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenActionIsContinue_IgnoresStepIndex()
        {
            // Arrange: human-in-the-loop chain paused awaiting approval. StepIndex has no meaning
            // for Action.Continue - it must be ignored even when set to an out-of-range value that
            // would fail ValidateStepIndex if RedoFromStep read it instead.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-continue-ignores-stepindex");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            // Act: StepIndex is set to a value that is out of range for this single-step session
            // (would throw if RedoFromStep's ValidateStepIndex ever ran against it), proving
            // Continue never even inspects it.
            var response = await engine.ResumeAsync(
                orchestrator,
                executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue, StepIndex = 99 });

            // Assert
            response.Status.Should().Be("completed");
            response.Output.Should().Be("final");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenActionIsReject_IgnoresStepIndex()
        {
            // Arrange: same as the plain-Reject test above, but with StepIndex additionally set to
            // an out-of-range value - Reject must ignore it entirely (it doesn't even receive
            // ResumeRequest, only the session and session id).
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-reject-ignores-stepindex");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true },
                checkpointing: new CheckpointingDefinition
                {
                    Enabled = true
                });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", firstClient)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });

            // Act
            var response = await engine.ResumeAsync(
                orchestrator,
                executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Reject, StepIndex = 99 });
            var checkpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);

            // Assert
            response.Status.Should().Be("rejected");
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("rejected");
        }

        [TestMethod]
        public async Task ExecuteAsync_WithExistingSessionId_ReusesExistingSession()
        {
            // Arrange
            var client = new RecordingChatClient((messages, _, _) =>
                $"done:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);
            var sessionStore = new InMemorySessionStore();
            var existingSession = sessionStore.Create(orchestrator.Id);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", client)
                });

            // Act
            var response = await engine.ExecuteAsync(
                orchestrator,
                new ExecuteRequest
                {
                    Input = "input",
                    SessionId = existingSession.SessionId
                });

            // Assert
            response.SessionId.Should().Be(existingSession.SessionId);
            sessionStore.Get(existingSession.SessionId).Should().BeSameAs(existingSession);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenPatternIsUnsupported_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);
            orchestrator.Pattern = "concurrent";

            var engine = CreateEngine(
                orchestrator,
                new InMemorySessionStore(),
                new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent(
                        "writer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task GetCheckpointsAsync_WhenCheckpointingIsDisabled_ReturnsNull()
        {
            // Arrange
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);
            var engine = CreateEngine(
                orchestrator,
                new InMemorySessionStore(),
                new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent(
                        "writer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var checkpoint = await engine.GetCheckpointsAsync(orchestrator, "missing");

            // Assert
            checkpoint.Should().BeNull();
        }

        [TestMethod]
        public async Task ResumeAsync_WhenSessionIsMissing_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);
            var engine = CreateEngine(
                orchestrator,
                new InMemorySessionStore(),
                new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent(
                        "writer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator,
                "missing-session",
                new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenSessionIsNotPendingApproval_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);
            var sessionStore = new InMemorySessionStore();
            var session = sessionStore.Create(orchestrator.Id);
            session.Status = "completed";
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent(
                        "writer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator,
                session.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenCheckpointingIsNotEnabled_ThrowsInvalidOperationException()
        {
            // Arrange: humanInLoop enabled without checkpointing enabled has no supported resume
            // mechanism (there is no non-checkpointed "live" resume path), so ResumeAsync must
            // reject it up front.
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true });
            var sessionStore = new InMemorySessionStore();
            var session = sessionStore.Create(orchestrator.Id);
            session.Status = "pending_approval";
            session.PendingOutput = "draft";

            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent(
                        "reviewer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator,
                session.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenCheckpointDocumentIsMissing_ThrowsInvalidOperationException()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-missing-checkpoint");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true },
                checkpointing: new CheckpointingDefinition
                {
                    Enabled = true
                });

            var sessionStore = new InMemorySessionStore();
            var session = sessionStore.Create(orchestrator.Id);
            session.Status = "pending_approval";

            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent(
                        "reviewer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator,
                session.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenCheckpointDocumentHasNoMafData_ThrowsInvalidOperationException()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-invalid-checkpoint");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true },
                checkpointing: new CheckpointingDefinition
                {
                    Enabled = true
                });

            var sessionStore = new InMemorySessionStore();
            var session = sessionStore.Create(orchestrator.Id);
            session.Status = "pending_approval";

            var store = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            await store.SaveAsync(new SessionCheckpointDocument
            {
                SessionId = session.SessionId,
                OrchestratorId = orchestrator.Id,
                Pattern = orchestrator.Pattern,
                Input = "input",
                Status = "pending_approval"
            });

            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent(
                        "reviewer",
                        new RecordingChatClient((_, _, _) => "unused"))
                });

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator,
                session.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        // -----------------------------------------------------------------------------------
        // Rewinding to an earlier step - re-executing it, and every step after it, discarding
        // their prior outputs - see WorkflowEngine.ResumeFromCheckpointAsync.
        // -----------------------------------------------------------------------------------

        [TestMethod]
        public async Task ResumeAsync_WhenRewindingCompletedSessionToEarlierStep_ReexecutesFromThatStepAndDiscardsLaterOutputs()
        {
            // Arrange: 3-agent sequential chain. Run to completion, then rewind to "second"'s
            // checkpoint (recorded right after "first" completed) - "second" and "third" must be
            // re-executed with fresh outputs, and "first"'s own output must be untouched.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-rewind-completed");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondCallCount = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
            {
                secondCallCount++;
                return $"reviewed{secondCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var thirdClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "third", Instructions = "Third.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient),
                    ["third"] = WorkflowTestDoubles.CreateAgent("third", thirdClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");
            executeResponse.Output.Should().Be("final:reviewed1:draft:input");

            var checkpointBeforeRewind = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            checkpointBeforeRewind.Should().NotBeNull();

            // Act: rewind to "second" (step index 1) - i.e. re-execute "second" and "third".
            var rewindResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            rewindResponse.Status.Should().Be("completed");
            rewindResponse.Steps.Should().HaveCount(3);
            rewindResponse.Steps![0].AgentName.Should().Be("first");
            rewindResponse.Steps[0].Output.Should().Be("draft:input", "the step at/before the rewind point keeps its original output");
            rewindResponse.Steps[1].AgentName.Should().Be("second");
            rewindResponse.Steps[1].Output.Should().Be("reviewed2:draft:input", "the rewound step must produce a fresh output, not reuse the old one");
            rewindResponse.Steps[2].AgentName.Should().Be("third");
            rewindResponse.Output.Should().Be("final:reviewed2:draft:input");
            secondCallCount.Should().Be(2, "\"second\" must be re-invoked exactly once by the rewind");

            var checkpointAfterRewind = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpointAfterRewind!.Status.Should().Be("completed");
            checkpointAfterRewind.Steps.Should().HaveCount(3);
            checkpointAfterRewind.Steps[1].Output.Should().Be("reviewed2:draft:input", "the old (pre-rewind) output for the rewound step must be discarded in the durable document too");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRewindingPendingApprovalSessionToEarlierStep_BypassesTheOutstandingReviewAndRewindsInstead()
        {
            // Arrange: 3-agent human-in-the-loop chain. Answer the first pending review
            // ("reviewer") so "editor" runs and pauses awaiting its own review. At that point
            // document.CheckpointId equals Steps[1] ("editor")'s checkpoint - distinct from
            // Steps[0] ("reviewer")'s checkpoint. Rewinding to Steps[0]'s checkpoint instead of
            // answering editor's pending review must bypass/discard that pending review rather
            // than trying to answer it, and fall back to a fresh run since no MAF checkpoint
            // exists before the very first agent.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-rewind-pending-approval");
            var reviewerCallCount = 0;
            var reviewerClient = new RecordingChatClient((messages, _, _) =>
            {
                reviewerCallCount++;
                return $"draft{reviewerCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var editorClient = new RecordingChatClient((messages, _, _) =>
                $"edited:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var publisherClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "editor", Instructions = "Edit.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "publisher", Instructions = "Publish.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", reviewerClient),
                    ["editor"] = WorkflowTestDoubles.CreateAgent("editor", editorClient),
                    ["publisher"] = WorkflowTestDoubles.CreateAgent("publisher", publisherClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");
            reviewerCallCount.Should().Be(1);

            // Answer reviewer's pending review so editor runs and pauses on its own review.
            var afterEditorResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });
            afterEditorResponse.Status.Should().Be("pending_approval");

            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            pendingCheckpoint!.Steps.Should().HaveCount(2);

            // Act: rewind to step 0 ("reviewer") instead of answering the pending review for
            // "editor".
            var rewindResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 0 });

            // Assert: "reviewer" is re-invoked (a fresh run), producing a new pending review -
            // editor's pending review is discarded, not answered.
            rewindResponse.Status.Should().Be("pending_approval");
            reviewerCallCount.Should().Be(2, "rewinding to step 0 must re-invoke \"reviewer\" via a fresh run");
            rewindResponse.Output.Should().Be("draft2:input");
            rewindResponse.Steps.Should().HaveCount(1, "the rewind discards editor's now-stale step and pauses again right after reviewer");

            var checkpoint = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpoint!.Status.Should().Be("pending_approval");
            checkpoint.PendingOutput.Should().Be("draft2:input");
            checkpoint.Steps.Should().HaveCount(1);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoingTheCurrentlyPendingStep_ReexecutesItFreshInsteadOfAnsweringItsReview()
        {
            // Arrange: regression coverage for the reported bug - naming the step CURRENTLY
            // awaiting review via StepIndex (rather than an earlier, already-completed step)
            // must genuinely re-execute that step (a fresh LLM call, discarding its previous
            // output), not be silently misread as "answer the pending review as-is." Two-agent
            // human-in-the-loop chain: "profiling" pauses for review after its first run;
            // RedoFromStep naming "profiling" (step 0, the one currently pending) must invoke it
            // again and pause again for review of the NEW output.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-pending-step");
            var profilingCallCount = 0;
            var profilingClient = new RecordingChatClient((messages, _, _) =>
            {
                profilingCallCount++;
                return $"profile{profilingCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var mappingClient = new RecordingChatClient((messages, _, _) =>
                $"mapped:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "profiling", Instructions = "Profile.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "mapping", Instructions = "Map.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["profiling"] = WorkflowTestDoubles.CreateAgent("profiling", profilingClient),
                    ["mapping"] = WorkflowTestDoubles.CreateAgent("mapping", mappingClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");
            profilingCallCount.Should().Be(1);

            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            pendingCheckpoint!.PendingStepIndex.Should().Be(0, "\"profiling\" (step 0) is the step currently awaiting review");
            pendingCheckpoint.Steps.Should().ContainSingle("the pending step is already recorded, awaiting its own review");

            // Act: redo step 0 ("profiling"), the step currently pending review - NOT an
            // already-completed earlier step. This must re-invoke "profiling" fresh rather than
            // routing into "answer the pending review."
            var redoResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 0 });

            // Assert: "profiling" was genuinely re-invoked (new LLM call, new output) and the
            // workflow paused again for review of THAT new output - "mapping" was never reached.
            profilingCallCount.Should().Be(2, "\"profiling\" must be re-invoked, not have its pending review silently answered");
            redoResponse.Status.Should().Be("pending_approval");
            redoResponse.Output.Should().Be("profile2:input");
            redoResponse.Steps.Should().ContainSingle("mapping must not have run - profiling's own review is pending again");

            var checkpointAfterRedo = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpointAfterRedo!.Status.Should().Be("pending_approval");
            checkpointAfterRedo.PendingOutput.Should().Be("profile2:input");
            checkpointAfterRedo.Steps.Should().ContainSingle();
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoingALaterStepAfterAnEarlierReviewWasAlreadyAnswered_FastForwardsThroughThatAlreadyResolvedGate()
        {
            // Arrange: regression coverage for the actually-reported live bug. Two-agent
            // human-in-the-loop chain ("profiling" step 0, "mapping" step 1). Answer "profiling"'s
            // review (Continue) so "mapping" runs and pauses on its OWN review - Steps.Count == 2.
            // RedoFromStep StepIndex=1 ("mapping") must genuinely re-invoke "mapping" (fast-
            // forwarding through "profiling"'s already-resolved review gate by replaying its
            // already-recorded answer), NOT silently re-pause back on "profiling"'s review.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-later-step-fastforward");
            var profilingCallCount = 0;
            var profilingClient = new RecordingChatClient((messages, _, _) =>
            {
                profilingCallCount++;
                return $"profile{profilingCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var mappingCallCount = 0;
            var mappingClient = new RecordingChatClient((messages, _, _) =>
            {
                mappingCallCount++;
                return $"mapped{mappingCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "profiling", Instructions = "Profile.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "mapping", Instructions = "Map.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["profiling"] = WorkflowTestDoubles.CreateAgent("profiling", profilingClient),
                    ["mapping"] = WorkflowTestDoubles.CreateAgent("mapping", mappingClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");
            profilingCallCount.Should().Be(1);

            // Approve "profiling"'s review so "mapping" runs and pauses on its own review. Both
            // agents have now completed their own execution - the pending step is already
            // recorded in Steps (with status "completed") while it awaits its own review, same as
            // "profiling" was at the very start.
            var afterMappingResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue });
            afterMappingResponse.Status.Should().Be("pending_approval");
            mappingCallCount.Should().Be(1);
            afterMappingResponse.Steps.Should().HaveCount(2, "\"profiling\" and \"mapping\" have both completed execution");
            afterMappingResponse.Steps![0].Output.Should().Be("profile1:input");
            afterMappingResponse.Steps[1].Output.Should().Be("mapped1:profile1:input");

            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            pendingCheckpoint!.PendingStepIndex.Should().Be(1, "\"mapping\" (step 1) is now the step awaiting review");

            // Act: redo step 1 ("mapping") - the step currently pending review, one step PAST
            // "profiling"'s already-answered review.
            var redoResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert: "mapping" was genuinely re-invoked using "profiling"'s already-approved
            // output as its input - proving the resume fast-forwarded past "profiling"'s
            // already-resolved gate instead of getting stuck re-pausing on it. "profiling" itself
            // must NOT have been re-invoked.
            profilingCallCount.Should().Be(1, "\"profiling\"'s already-answered review must not be re-asked or re-run");
            mappingCallCount.Should().Be(2, "\"mapping\" must be genuinely re-invoked by the redo");
            redoResponse.Status.Should().Be("pending_approval");
            redoResponse.Output.Should().Be("mapped2:profile1:input");
            redoResponse.Steps.Should().HaveCount(2, "\"profiling\"'s completed step remains, plus \"mapping\"'s freshly re-executed (still-pending-review) step");
            redoResponse.Steps![0].Output.Should().Be("profile1:input", "\"profiling\"'s recorded output is untouched by the redo");
            redoResponse.Steps[1].Output.Should().Be("mapped2:profile1:input", "\"mapping\" must produce a NEW output, not reuse its stale first-run output");

            var checkpointAfterRedo = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpointAfterRedo!.Status.Should().Be("pending_approval");
            checkpointAfterRedo.PendingOutput.Should().Be("mapped2:profile1:input");
            checkpointAfterRedo.Steps.Should().HaveCount(2);
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoFromStepNamesTheNotYetExecutedNextStepWhilePendingApproval_ThrowsInvalidOperationException()
        {
            // Arrange: regression coverage for the adjacent, distinct invalid combination -
            // RedoFromStep naming the step that has NEVER executed yet (StepIndex == Steps.Count)
            // while the session is pending_approval on an earlier step's review. There is nothing
            // to "redo" there; the caller should use Action.Continue instead to answer the pending
            // review and let that next step run for the first time.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-guard-not-yet-executed-step");
            var client = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "profiling", Instructions = "Profile.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "mapping", Instructions = "Map.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["profiling"] = WorkflowTestDoubles.CreateAgent("profiling", client),
                    ["mapping"] = WorkflowTestDoubles.CreateAgent("mapping", client)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            pendingCheckpoint!.Steps.Should().ContainSingle("only \"profiling\" has completed; \"mapping\" (step 1) has never executed");

            // Act: StepIndex 1 names "mapping" - the not-yet-executed next step - while still
            // pending_approval on "profiling"'s review.
            var action = () => engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            thrown.Message.Should().Contain("Action.Continue");

            // The session/document must be left untouched by the rejected request.
            var checkpointAfterThrow = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            checkpointAfterThrow!.Status.Should().Be("pending_approval");
            checkpointAfterThrow.Steps.Should().ContainSingle();
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRewindingWithInputOverrideToFirstStep_UsesOverriddenInputInsteadOfOriginal()
        {
            // Arrange: single-agent orchestrator. Complete it once, then rewind to step 0 with an
            // EditedOutput input override - the agent must see the overridden input, not the
            // session's original one.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-rewind-input-override");
            var client = new RecordingChatClient((messages, _, _) =>
                $"done:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent> { ["writer"] = WorkflowTestDoubles.CreateAgent("writer", client) },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "original input" });
            executeResponse.Status.Should().Be("completed");
            executeResponse.Output.Should().Be("done:original input");

            var checkpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);
            checkpoint.Should().NotBeNull();

            // Act: rewind to step 0 (the only step) with an overridden input.
            var rewindResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 0, EditedOutput = "overridden input" });

            // Assert
            rewindResponse.Status.Should().Be("completed");
            rewindResponse.Output.Should().Be("done:overridden input");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoingALaterStepWithEditedOutput_OverridesThePriorStepsOutputBeforeReplay()
        {
            // Arrange: same two-agent HITL setup as the "fast-forwards through that already-
            // resolved gate" test above, but this time supplying EditedOutput on the StepIndex=1
            // redo - "mapping" must re-run against the EDITED "profiling" output, not the
            // originally-recorded one, and that edit must be durably persisted onto step 0.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-later-step-edited-output");
            var profilingCallCount = 0;
            var profilingClient = new RecordingChatClient((messages, _, _) =>
            {
                profilingCallCount++;
                return $"profile{profilingCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var mappingCallCount = 0;
            var mappingClient = new RecordingChatClient((messages, _, _) =>
            {
                mappingCallCount++;
                return $"mapped{mappingCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "profiling", Instructions = "Profile.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "mapping", Instructions = "Map.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["profiling"] = WorkflowTestDoubles.CreateAgent("profiling", profilingClient),
                    ["mapping"] = WorkflowTestDoubles.CreateAgent("mapping", mappingClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            // Approve "profiling"'s review so "mapping" runs and pauses on its own review -
            // session is now paused at step-review-1, with both steps recorded.
            var afterMappingResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue });
            afterMappingResponse.Status.Should().Be("pending_approval");
            afterMappingResponse.Steps.Should().HaveCount(2);

            // Act: redo step 1 ("mapping"), supplying an edited replacement for "profiling"'s
            // (step 0's) recorded output.
            var redoResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1, EditedOutput = "edited-profile" });

            // Assert: "mapping" is genuinely re-invoked against the EDITED prior output, not the
            // stale original "profile1:input" text. "profiling" itself must not be re-invoked.
            profilingCallCount.Should().Be(1, "\"profiling\" must not be re-run just because its recorded output was edited");
            mappingCallCount.Should().Be(2, "\"mapping\" must be genuinely re-invoked by the redo");
            redoResponse.Status.Should().Be("pending_approval");
            redoResponse.Output.Should().Be("mapped2:edited-profile");
            redoResponse.Steps.Should().HaveCount(2);
            redoResponse.Steps![0].Output.Should().Be("edited-profile", "\"profiling\"'s recorded output must reflect the EditedOutput override");
            redoResponse.Steps[1].Output.Should().Be("mapped2:edited-profile");

            var checkpointAfterRedo = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpointAfterRedo!.Status.Should().Be("pending_approval");
            checkpointAfterRedo.Steps.Should().HaveCount(2);
            checkpointAfterRedo.Steps[0].Output.Should().Be("edited-profile", "the override must be durably persisted onto step 0's checkpoint record");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoFromStepIsAPlainContinueWithEditedOutput_ThrowsInvalidOperationException()
        {
            // Arrange: regression coverage - EditedOutput on a StepIndex > 0 RedoFromStep only
            // makes sense for a genuine rewind (an already-completed step). A "plain continue"
            // (StepIndex == Steps.Count, e.g. resuming a crashed/failed run) has no "prior step"
            // concept for EditedOutput to override, so it must be rejected rather than silently
            // ignored. Reuses the same "failed partway" setup as
            // ResumeAsync_WhenSessionFailedPartway_ContinuesFromLastCheckpointAndCompletes (no
            // human-in-the-loop involved - genuinely "failed" status is cleanly reachable there,
            // unlike a HITL chain where a mid-run failure during Resume bypasses failure
            // recording - see ResumeAsync's dedicated "rethrown as-is" InvalidOperationException
            // catch clause).
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-plain-continue-edited-output-guard");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondAttempt = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
                Interlocked.Increment(ref secondAttempt) == 1
                    ? throw new InvalidOperationException("transient failure")
                    : $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: checkpointStore);

            // "second" throws on its first invocation, leaving the session "failed" with
            // Steps.Count == 1 (only "first" completed) - StepIndex 1 names the not-yet-completed
            // "second", i.e. a plain continue, not a rewind.
            var exception = await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(() =>
                engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }));
            var sessionId = exception.SessionId;

            var afterFailure = await checkpointStore.LoadAsync(sessionId);
            afterFailure!.Status.Should().Be("failed");
            afterFailure.Steps.Should().ContainSingle();

            // Act: StepIndex 1 == Steps.Count is a plain continue (nothing to redo - "second"
            // never completed), so EditedOutput has no valid target here.
            var action = () => engine.ResumeAsync(
                orchestrator, sessionId,
                new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1, EditedOutput = "edited-first" });

            // Assert
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            thrown.Message.Should().Contain("EditedOutput");

            // The session/document must be left untouched by the rejected request.
            var checkpointAfterThrow = await checkpointStore.LoadAsync(sessionId);
            checkpointAfterThrow!.Status.Should().Be("failed");
            checkpointAfterThrow.Steps.Should().ContainSingle();
            checkpointAfterThrow.Steps[0].Output.Should().Be("draft:input", "the rejected request must not have mutated step 0's output");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoFromStepHasEditedOutputButHumanInLoopIsDisabled_ThrowsInvalidOperationException()
        {
            // Arrange: EditedOutput's "override the prior step's output" meaning relies on the
            // human-in-the-loop review-gate replay mechanism to actually carry the override into
            // the resumed run - there is no equivalent hook in a plain (non-HITL) sequential
            // graph, so this combination must be rejected rather than silently ignored.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-redo-no-hitl-edited-output-guard");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");
            executeResponse.Steps.Should().HaveCount(2);

            // Act: genuine rewind (StepIndex=1 < Steps.Count=2) but no human-in-the-loop, so
            // there is no review-gate mechanism to carry an EditedOutput override forward.
            var action = () => engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1, EditedOutput = "edited-first" });

            // Assert
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            thrown.Message.Should().Contain("EditedOutput");

            var checkpointAfterThrow = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpointAfterThrow!.Status.Should().Be("completed");
            checkpointAfterThrow.Steps.Should().HaveCount(2);
            checkpointAfterThrow.Steps[0].Output.Should().Be("draft:input", "the rejected request must not have mutated step 0's output");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenAnsweringPendingReview_EditedOutputRetainsItsAnswerModeMeaningRegardlessOfRewindSupport()
        {
            // Arrange: regression coverage for the dispatch refactor - answering a pending review
            // via Action.Continue must still use EditedOutput as the pending step's edited
            // output (its answer-mode meaning), not as a rewind input override.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-answer-review-regression");
            var reviewerClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var publisherClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "publisher", Instructions = "Publish.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", reviewerClient),
                    ["publisher"] = WorkflowTestDoubles.CreateAgent("publisher", publisherClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            // Act
            var response = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue, EditedOutput = "edited draft" });

            // Assert: the edit was applied to the reviewer step, but human-in-the-loop pauses
            // after every step, so publisher's own review is still outstanding.
            response.Status.Should().Be("pending_approval");
            response.Steps![0].Output.Should().Be("edited draft");

            // Answer publisher's own pending review too, to reach completion.
            var finalResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId,
                new ResumeRequest { Action = ResumeAction.Continue });

            finalResponse.Status.Should().Be("completed");
            finalResponse.Output.Should().Be("final:edited draft");
        }

        // -----------------------------------------------------------------------------------
        // Resuming a "running"/"failed" checkpointed run from its last completed step (no
        // human-in-the-loop approval involved) - see WorkflowEngine.ResumeFromCheckpointAsync.
        // -----------------------------------------------------------------------------------

        [TestMethod]
        public async Task ResumeAsync_WhenSessionFailedPartway_ContinuesFromLastCheckpointAndCompletes()
        {
            // Arrange: "first" always succeeds; "second" throws on its very first call (simulating
            // a transient failure) but succeeds on any subsequent call (i.e. once resumed).
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-failed");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondAttempt = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
                Interlocked.Increment(ref secondAttempt) == 1
                    ? throw new InvalidOperationException("transient failure")
                    : $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: checkpointStore);

            // Act (1): the run fails on "second" - "first"'s step must already be durably
            // persisted (proving CheckpointId/steps are captured incrementally, not just at the
            // end of a successful run - see WorkflowEngine.PersistCompletedStepAsync), even though
            // the overall call throws.
            var exception = await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(() =>
                engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }));
            var sessionId = exception.SessionId;

            var afterFailure = await checkpointStore.LoadAsync(sessionId);
            afterFailure.Should().NotBeNull();
            afterFailure!.Status.Should().Be("failed");
            afterFailure.CheckpointId.Should().NotBeNullOrEmpty("the last completed step's checkpoint must be captured even though a later step failed");
            afterFailure.Steps.Should().ContainSingle(s => s.AgentName == "first" && s.Output == "draft:input");

            // Act (2): resume - "second" now succeeds, continuing from the last checkpoint rather
            // than re-running "first". StepIndex == Steps.Count (1) means "plain continue," not a
            // rewind - nothing to redo since "second" never completed.
            var resumeResponse = await engine.ResumeAsync(orchestrator, sessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            resumeResponse.Status.Should().Be("completed");
            resumeResponse.Output.Should().Be("final:draft:input");
            resumeResponse.Steps.Should().HaveCount(2);
            resumeResponse.Steps![0].AgentName.Should().Be("first");
            resumeResponse.Steps[1].AgentName.Should().Be("second");
            secondAttempt.Should().Be(2, "\"second\" should only be re-invoked once by the resume, not by the original failed run retrying itself");

            var afterResume = await checkpointStore.LoadAsync(sessionId);
            afterResume.Should().NotBeNull();
            afterResume!.Status.Should().Be("completed");
            afterResume.FinalOutput.Should().Be("final:draft:input");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenSessionLeftRunningAfterSimulatedCrash_RehydratesAndCompletesWithoutInMemorySession()
        {
            // Arrange: same flaky "second" agent as above, but this time we simulate a genuine
            // process crash - i.e. the checkpoint document is left at "running" (never reached
            // FinalizeCheckpointAsync's "failed" write) and the in-memory session is gone entirely
            // (a brand-new WorkflowEngine/ISessionStore, sharing only the durable checkpoint
            // store/directory, stands in for "the process restarted").
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-crashed");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondAttempt = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
                Interlocked.Increment(ref secondAttempt) == 1
                    ? throw new InvalidOperationException("transient failure")
                    : $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var mafCheckpointStore = new InMemoryJsonCheckpointStore();
            var crashedEngine = CreateEngine(
                orchestrator,
                new InMemorySessionStore(),
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: checkpointStore,
                mafCheckpointStore: mafCheckpointStore);

            var exception = await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(() =>
                crashedEngine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }));
            var sessionId = exception.SessionId;

            // Simulate "the process crashed before it could record the failure": the durable
            // checkpoint/step data from "first" is real (already incrementally persisted), but we
            // overwrite the terminal status back to "running" as it would have been left by
            // PersistCompletedStepAsync alone, had the process died before reaching
            // ExecuteAsync's catch block.
            var crashedDocument = await checkpointStore.LoadAsync(sessionId);
            crashedDocument.Should().NotBeNull();
            crashedDocument!.Status = "running";
            crashedDocument.Error = null;
            await checkpointStore.SaveAsync(crashedDocument);

            // Act: a fresh engine/session-store combination (same checkpoint store/directory) -
            // ISessionStore.Get(sessionId) will return null here, forcing rehydration from the
            // checkpoint document rather than throwing "session not found".
            var restartedSessionStore = new InMemorySessionStore();
            restartedSessionStore.Get(sessionId).Should().BeNull();
            var restartedEngine = CreateEngine(
                orchestrator,
                restartedSessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: checkpointStore,
                mafCheckpointStore: mafCheckpointStore);

            var resumeResponse = await restartedEngine.ResumeAsync(orchestrator, sessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            resumeResponse.Status.Should().Be("completed");
            resumeResponse.Output.Should().Be("final:draft:input");
            resumeResponse.Steps.Should().HaveCount(2, "the rehydrated session must include the pre-crash \"first\" step, not just steps produced after resume");
            resumeResponse.Steps![0].AgentName.Should().Be("first");
            resumeResponse.Steps[0].Output.Should().Be("draft:input");
            resumeResponse.Steps[1].AgentName.Should().Be("second");

            restartedSessionStore.Get(sessionId).Should().NotBeNull("resume must register the rehydrated session so later status queries see it");

            var afterResume = await checkpointStore.LoadAsync(sessionId);
            afterResume!.Status.Should().Be("completed");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenBothDurableStoresAreFileBackedAndFromASeparateInstance_ResumesWithoutAnySharedInMemoryState()
        {
            // Arrange: this is the direct regression test for the scenario the file-based durable
            // stores must handle correctly - two independent "orchestrator instances" (fresh
            // WorkflowEngine, fresh ISessionStore, and fresh store instances) sharing nothing
            // except the same underlying checkpoint directories on disk (standing in for two
            // processes/replicas sharing a mounted volume). Before "instance A crashes",
            // "second"'s MAF graph-level checkpoint and the step-level manifest must both have
            // been durably flushed to those shared directories so that "instance B" (which never
            // shared any in-memory state with "instance A") can resume purely from disk.
            using var manifestDirectory = new TestArtifactDirectory("workflow-engine-resume-file-backed-manifest");
            using var mafDirectory = new TestArtifactDirectory("workflow-engine-resume-file-backed-maf");

            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondAttempt = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
                Interlocked.Increment(ref secondAttempt) == 1
                    ? throw new InvalidOperationException("transient failure")
                    : $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            // "Instance A": its own JsonFileWorkflowCheckpointStore/FileSystemJsonCheckpointStore
            // (pointed at the shared directories), its own in-memory session store.
            using var instanceAMafStore = new FileSystemJsonCheckpointStore(new DirectoryInfo(mafDirectory.Path));
            var instanceAEngine = CreateEngine(
                orchestrator,
                new InMemorySessionStore(),
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(manifestDirectory.Path),
                mafCheckpointStore: instanceAMafStore);

            var exception = await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(() =>
                instanceAEngine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }));
            var sessionId = exception.SessionId;

            // Simulate "instance A crashed": the document is left at "running" rather than the
            // "failed" write ExecuteAsync's catch block would otherwise have made.
            var instanceACheckpointStore = new JsonFileWorkflowCheckpointStore(manifestDirectory.Path);
            var crashedDocument = await instanceACheckpointStore.LoadAsync(sessionId);
            crashedDocument.Should().NotBeNull();
            crashedDocument!.Status = "running";
            crashedDocument.Error = null;
            await instanceACheckpointStore.SaveAsync(crashedDocument);

            // "Instance A" is now done with the MAF store - dispose it so its file handles
            // (e.g. index.jsonl) are released before "instance B" opens the same directory.
            instanceAMafStore.Dispose();

            // Act: "instance B" - completely separate store/engine/session-store instances,
            // sharing only the same underlying directories on disk. No object here was ever
            // touched by "instance A".
            var instanceBSessionStore = new InMemorySessionStore();
            instanceBSessionStore.Get(sessionId).Should().BeNull();
            using var instanceBMafStore = new FileSystemJsonCheckpointStore(new DirectoryInfo(mafDirectory.Path));
            var instanceBEngine = CreateEngine(
                orchestrator,
                instanceBSessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient)
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(manifestDirectory.Path),
                mafCheckpointStore: instanceBMafStore);

            var resumeResponse = await instanceBEngine.ResumeAsync(orchestrator, sessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            resumeResponse.Status.Should().Be("completed");
            resumeResponse.Output.Should().Be("final:draft:input");
            resumeResponse.Steps.Should().HaveCount(2, "the rehydrated session must include the pre-crash \"first\" step");
            resumeResponse.Steps![0].AgentName.Should().Be("first");
            resumeResponse.Steps[1].AgentName.Should().Be("second");

            var afterResume = await new JsonFileWorkflowCheckpointStore(manifestDirectory.Path).LoadAsync(sessionId);
            afterResume!.Status.Should().Be("completed");
            afterResume.FinalOutput.Should().Be("final:draft:input");

            // Release "instance B"'s handles before the TestArtifactDirectory `using`s clean up.
            instanceBMafStore.Dispose();
        }

        [TestMethod]
        public async Task ResumeAsync_WhenActionIsContinue_AnswersPendingReview()
        {
            // Arrange: human-in-the-loop chain paused awaiting approval. Action.Continue (with
            // no StepIndex) must answer the pending review as-is.
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-continue-answers-review");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            // Act
            var response = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            response.Status.Should().Be("completed");
            response.Output.Should().Be("final");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenRedoFromStepIsRequestedWithoutStepIndex_ThrowsInvalidOperationException()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-redo-missing-stepindex");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");

            // Act: RedoFromStep without StepIndex.
            var action = () => engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep });

            // Assert
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            thrown.Message.Should().Contain("StepIndex is required");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenStepIndexNamesAnEarlierStep_RewindsFromThatStep()
        {
            // Arrange: 3-agent sequential chain, rewinding purely via Action.RedoFromStep +
            // StepIndex - the recommended, unambiguous way to say "redo this step."
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-rewind-stepindex");
            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondCallCount = 0;
            var secondClient = new RecordingChatClient((messages, _, _) =>
            {
                secondCallCount++;
                return $"reviewed{secondCallCount}:{WorkflowTestDoubles.GetLatestUserText(messages)}";
            });
            var thirdClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "first", Instructions = "First.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "second", Instructions = "Second.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "third", Instructions = "Third.", Provider = "test-provider", Model = "test-model" }
                ],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["first"] = WorkflowTestDoubles.CreateAgent("first", firstClient),
                    ["second"] = WorkflowTestDoubles.CreateAgent("second", secondClient),
                    ["third"] = WorkflowTestDoubles.CreateAgent("third", thirdClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");

            // Act: StepIndex 1 means "redo 'second' (stepIndex 1)" - re-executing it and "third".
            var rewindResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 1 });

            // Assert
            rewindResponse.Status.Should().Be("completed");
            rewindResponse.Steps.Should().HaveCount(3);
            rewindResponse.Steps![0].Output.Should().Be("draft:input", "the step before the rewind point keeps its original output");
            rewindResponse.Steps[1].Output.Should().Be("reviewed2:draft:input", "the rewound step must produce a fresh output, not reuse the old one");
            rewindResponse.Output.Should().Be("final:reviewed2:draft:input");
            secondCallCount.Should().Be(2, "\"second\" must be re-invoked exactly once by the rewind");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenStepIndexIsOutOfRange_ThrowsInvalidOperationException()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-stepindex-out-of-range");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");

            // Act: only step 0 exists ("writer"); step 5 is out of range.
            var action = () => engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.RedoFromStep, StepIndex = 5 });

            // Assert
            var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            thrown.Message.Should().Contain("out of range");
        }

        [TestMethod]
        public async Task DeleteCheckpointAsync_WhenCheckpointingDisabled_ReturnsFalse()
        {
            // Arrange
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }]);

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "output"))
                });

            // Act
            var deleted = await engine.DeleteCheckpointAsync(orchestrator, "unknown-session");

            // Assert
            deleted.Should().BeFalse();
        }

        [TestMethod]
        public async Task DeleteCheckpointAsync_WhenNoCheckpointExistsForSession_ReturnsFalse()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-delete-checkpoint");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "output"))
                },
                checkpointStore: new JsonFileWorkflowCheckpointStore(artifactDirectory.Path));

            // Act
            var deleted = await engine.DeleteCheckpointAsync(orchestrator, "never-executed-session");

            // Assert
            deleted.Should().BeFalse();
        }

        [TestMethod]
        public async Task DeleteCheckpointAsync_WhenSessionCompleted_DeletesCheckpointAndReturnsTrue()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-delete-checkpoint");
            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var mafCheckpointStore = new InMemoryJsonCheckpointStore();
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "output"))
                },
                checkpointStore: checkpointStore,
                mafCheckpointStore: mafCheckpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");
            mafCheckpointStore.CountFor(executeResponse.SessionId).Should().BePositive(
                "the session must have at least one MAF graph-level checkpoint recorded before we can assert it gets cleaned up");

            // Act
            var deleted = await engine.DeleteCheckpointAsync(orchestrator, executeResponse.SessionId);

            // Assert
            deleted.Should().BeTrue();
            (await checkpointStore.LoadAsync(executeResponse.SessionId)).Should().BeNull();
            mafCheckpointStore.CountFor(executeResponse.SessionId).Should().Be(0,
                "deleting a session's checkpoint must also clean up its MAF graph-level checkpoints, not just the step manifest");
        }

        [TestMethod]
        public async Task DeleteCheckpointAsync_WhenSessionPendingApproval_ThrowsInvalidOperationException()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-delete-checkpoint");
            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "publisher", Instructions = "Publish.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var checkpointStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", new RecordingChatClient((_, _, _) => "draft")),
                    ["publisher"] = WorkflowTestDoubles.CreateAgent("publisher", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");

            // Act
            var action = () => engine.DeleteCheckpointAsync(orchestrator, executeResponse.SessionId);

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            (await checkpointStore.LoadAsync(executeResponse.SessionId)).Should().NotBeNull();
        }

        // -----------------------------------------------------------------------------------
        // Cancellation-resilient checkpoint persistence (see WorkflowEngine.CreatePersistenceCts)
        // -----------------------------------------------------------------------------------

        [TestMethod]
        public async Task ResumeAsync_WhenCallerTokenIsCancelledDuringPersistence_StillPersistsCheckpointAndCompletes()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-cancel");
            using var cts = new CancellationTokenSource();

            var firstClient = new RecordingChatClient((messages, _, _) =>
                $"draft:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var secondClient = new RecordingChatClient((messages, _, _) =>
                $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}");

            var orchestrator = CreateOrchestrator(
                agents:
                [
                    new AgentDefinition { Name = "reviewer", Instructions = "Review.", Provider = "test-provider", Model = "test-model" },
                    new AgentDefinition { Name = "publisher", Instructions = "Publish.", Provider = "test-provider", Model = "test-model" }
                ],
                humanInLoop: new HumanInLoopDefinition { Enabled = true, ApprovalPrompt = "Approve this output." },
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            // Execute (which pauses after "reviewer") makes 3 checkpoint saves: the initial
            // creation save, the per-completed-step save recording the reviewer step the moment
            // it completes (mid-drain - see PersistCompletedStepAsync), and the pause-path save
            // of reviewer's pending state once the review request is detected. The *5th* save
            // call overall is this resume's own pause-path save, made after the publisher agent
            // has already produced its result *and* its own review pause has already been fully
            // detected (so the drain itself has already finished by the time this save runs) -
            // the exact point in production where an ingress/gateway timeout (or client
            // disconnect) firing mid-run would cancel the caller's own token just as the engine
            // tries to persist, without being able to also truncate the run's own event drain.
            var checkpointStore = new CancelCallerOnNthSaveCheckpointStoreDecorator(
                new JsonFileWorkflowCheckpointStore(artifactDirectory.Path), cts, cancelOnSaveNumber: 5);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["reviewer"] = WorkflowTestDoubles.CreateAgent("reviewer", firstClient),
                    ["publisher"] = WorkflowTestDoubles.CreateAgent("publisher", secondClient)
                },
                checkpointStore: checkpointStore);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("pending_approval");
            var pendingCheckpoint = await engine.GetCheckpointsAsync(orchestrator, executeResponse.SessionId);

            // Act
            var resumeResponse = await engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue }, cts.Token);

            // Assert
            cts.IsCancellationRequested.Should().BeTrue("the 5th checkpoint save cancels it just before persisting");
            resumeResponse.Status.Should().Be("pending_approval");
            resumeResponse.Output.Should().Be("final:draft:input");

            var checkpoint = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("pending_approval");
            checkpoint.Steps.Should().HaveCount(2, "both agent steps must be recorded despite the cancelled caller token");
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenCallerTokenIsCancelledDuringPersistence_StillPersistsCheckpointAndCompletes()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-execute-cancel");
            using var cts = new CancellationTokenSource();

            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            // Save #1 is the initial checkpoint-creation write (before the agent even runs);
            // save #2 is the per-completed-step save made mid-drain the moment the (single) agent
            // finishes (see PersistCompletedStepAsync) - cancelling there could still truncate the
            // run's own event drain before it observes the terminal output event, so instead this
            // targets save #3, the FinalizeCheckpointAsync write made *after* the whole run has
            // already finished draining - the exact point in production where an ingress/gateway
            // timeout (or client disconnect) firing right as the engine finishes persisting the
            // already-fully-computed result shouldn't be able to lose that result.
            var checkpointStore = new CancelCallerOnNthSaveCheckpointStoreDecorator(
                new JsonFileWorkflowCheckpointStore(artifactDirectory.Path), cts, cancelOnSaveNumber: 3);
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((messages, _, _) =>
                        $"final:{WorkflowTestDoubles.GetLatestUserText(messages)}"))
                },
                checkpointStore: checkpointStore);

            // Act
            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" }, cts.Token);

            // Assert
            cts.IsCancellationRequested.Should().BeTrue();
            executeResponse.Status.Should().Be("completed");
            executeResponse.Output.Should().Be("final:input");

            var checkpoint = await checkpointStore.LoadAsync(executeResponse.SessionId);
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("completed");
            checkpoint.FinalOutput.Should().Be("final:input");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenCallerCancellationCausesFailure_LogsWarningAndStillPersistsFailureState()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-cancel-failure");
            using var cts = new CancellationTokenSource();
            var loggerMock = new Mock<ILogger<WorkflowEngine>>();

            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var realStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            // The *first* LoadAsync call inside ResumeCheckpointedAsync (fetching the checkpoint
            // to resume from) surfaces an OperationCanceledException against the caller's own
            // token, precisely mirroring the reported bug's stack trace (an OperationCanceledException
            // out of the checkpoint store) while keeping the caller token itself cancelled, so
            // ResumeAsync's catch block takes the "caller cancelled" branch.
            var checkpointStore = new FaultOnFirstLoadCheckpointStoreDecorator(
                realStore, () => { cts.Cancel(); return new OperationCanceledException(cts.Token); });
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: checkpointStore,
                logger: loggerMock.Object);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");

            // Manually move the session back to pending-approval so ResumeAsync's precondition
            // check passes and the fault-injected checkpoint load is actually reached.
            var session = sessionStore.Get(executeResponse.SessionId)!;
            session.Status = "pending_approval";

            // Act
            var action = () => engine.ResumeAsync(
                orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue }, cts.Token);

            // Assert
            await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(action);

            VerifyLog(loggerMock, LogLevel.Warning, Times.Once());
            VerifyLog(loggerMock, LogLevel.Error, Times.Never());

            var checkpoint = await realStore.LoadAsync(executeResponse.SessionId);
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("failed", "the failure state must still be recorded even though the caller's token is already cancelled");
        }

        [TestMethod]
        public async Task ResumeAsync_WhenAGenuineFailureOccursWithoutCallerCancellation_LogsErrorAndPersistsFailureState()
        {
            // Arrange
            using var artifactDirectory = new TestArtifactDirectory("workflow-engine-resume-genuine-failure");
            var loggerMock = new Mock<ILogger<WorkflowEngine>>();

            var orchestrator = CreateOrchestrator(
                agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

            var sessionStore = new InMemorySessionStore();
            var realStore = new JsonFileWorkflowCheckpointStore(artifactDirectory.Path);
            // A genuine (non-cancellation) fault - e.g. a real DB error - unrelated to the
            // caller's own token, which is never cancelled in this test.
            var checkpointStore = new FaultOnFirstLoadCheckpointStoreDecorator(
                realStore, () => new TimeoutException("checkpoint store unavailable"));
            var engine = CreateEngine(
                orchestrator,
                sessionStore,
                agentsByName: new Dictionary<string, AIAgent>
                {
                    ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                },
                checkpointStore: checkpointStore,
                logger: loggerMock.Object);

            var executeResponse = await engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });
            executeResponse.Status.Should().Be("completed");

            var session = sessionStore.Get(executeResponse.SessionId)!;
            session.Status = "pending_approval";

            // Act
            var action = () => engine.ResumeAsync(orchestrator, executeResponse.SessionId, new ResumeRequest { Action = ResumeAction.Continue });

            // Assert
            await Assert.ThrowsExactlyAsync<OrchestratorExecutionException>(action);

            VerifyLog(loggerMock, LogLevel.Error, Times.Once());
            VerifyLog(loggerMock, LogLevel.Warning, Times.Never());

            var checkpoint = await realStore.LoadAsync(executeResponse.SessionId);
            checkpoint.Should().NotBeNull();
            checkpoint!.Status.Should().Be("failed");
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenCheckpointStoreHangs_TimesOutInsteadOfBlockingIndefinitely()
        {
            // Arrange: shrink the persistence bound for this test only, so a genuinely stuck
            // store fails fast rather than the test waiting out the real 30s production timeout.
            var originalTimeout = WorkflowEngine.PersistenceTimeout;
            WorkflowEngine.PersistenceTimeout = TimeSpan.FromMilliseconds(100);
            try
            {
                using var artifactDirectory = new TestArtifactDirectory("workflow-engine-hanging-store");
                var orchestrator = CreateOrchestrator(
                    agents: [new AgentDefinition { Name = "writer", Instructions = "Write.", Provider = "test-provider", Model = "test-model" }],
                checkpointing: new CheckpointingDefinition { Enabled = true });

                var sessionStore = new InMemorySessionStore();
                var engine = CreateEngine(
                    orchestrator,
                    sessionStore,
                    agentsByName: new Dictionary<string, AIAgent>
                    {
                        ["writer"] = WorkflowTestDoubles.CreateAgent("writer", new RecordingChatClient((_, _, _) => "final"))
                    },
                    checkpointStore: new HangingWorkflowCheckpointStore());

                // Act
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Func<Task> act = () => engine.ExecuteAsync(orchestrator, new ExecuteRequest { Input = "input" });

                // Assert: this proves the bound is real - without it, a stuck store would hang
                // this test (and a production request) forever instead of failing within bound.
                await act.Should().ThrowAsync<Exception>();
                stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            }
            finally
            {
                WorkflowEngine.PersistenceTimeout = originalTimeout;
            }
        }

        /// <summary>Verifies via Moq's standard <see cref="ILogger"/> extension-method pattern that a log call at <paramref name="level"/> occurred the expected number of times.</summary>
        private static void VerifyLog(Mock<ILogger<WorkflowEngine>> loggerMock, LogLevel level, Times times)
        {
            // CA1873 false positive: this is a Moq expression tree used purely for verification,
            // not an actual logging call, so the "expensive argument evaluation" concern doesn't apply.
#pragma warning disable CA1873
            loggerMock.Verify(
                logger => logger.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((@object, @type) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);
#pragma warning restore CA1873
        }

        /// <summary>
        /// An <see cref="IWorkflowCheckpointStore"/> decorator that cancels a caller-supplied
        /// <see cref="CancellationTokenSource"/> right as its <paramref name="cancelOnSaveNumber"/>-th
        /// <see cref="SaveAsync"/> call begins (before delegating to <paramref name="inner"/>),
        /// deterministically reproducing "the caller's connection drops right as the engine tries
        /// to persist" without perturbing the agent/workflow run itself (which has already
        /// finished producing its result by the time any checkpoint save happens).
        /// </summary>
        private sealed class CancelCallerOnNthSaveCheckpointStoreDecorator(
            IWorkflowCheckpointStore inner, CancellationTokenSource cancelOnSave, int cancelOnSaveNumber) : IWorkflowCheckpointStore
        {
            private int _saveCount;

            public Task SaveAsync(SessionCheckpointDocument document, CancellationToken ct = default)
            {
                if (Interlocked.Increment(ref _saveCount) == cancelOnSaveNumber)
                    cancelOnSave.Cancel();

                return inner.SaveAsync(document, ct);
            }

            public Task<SessionCheckpointDocument?> LoadAsync(string sessionId, CancellationToken ct = default) =>
                inner.LoadAsync(sessionId, ct);

            public Task DeleteAsync(string sessionId, CancellationToken ct = default) => inner.DeleteAsync(sessionId, ct);
        }

        /// <summary>
        /// An <see cref="IWorkflowCheckpointStore"/> decorator whose very first <see cref="LoadAsync"/>
        /// call throws the exception produced by <paramref name="fault"/> instead of delegating to
        /// <paramref name="inner"/> - simulating a failure (e.g. a cancelled or broken connection)
        /// on the checkpoint read that starts a resume. Every subsequent call (in particular, the
        /// failure-state Load/Save made by WorkflowEngine's own failure-recording path) delegates
        /// to <paramref name="inner"/> normally, so the durable recording of that failure can
        /// still be asserted against the real store.
        /// </summary>
        private sealed class FaultOnFirstLoadCheckpointStoreDecorator(
            IWorkflowCheckpointStore inner, Func<Exception> fault) : IWorkflowCheckpointStore
        {
            private bool _hasFaulted;

            public Task<SessionCheckpointDocument?> LoadAsync(string sessionId, CancellationToken ct = default)
            {
                if (!_hasFaulted)
                {
                    _hasFaulted = true;
                    throw fault();
                }

                return inner.LoadAsync(sessionId, ct);
            }

            public Task SaveAsync(SessionCheckpointDocument document, CancellationToken ct = default) => inner.SaveAsync(document, ct);

            public Task DeleteAsync(string sessionId, CancellationToken ct = default) => inner.DeleteAsync(sessionId, ct);
        }

        /// <summary>
        /// An <see cref="IWorkflowCheckpointStore"/> whose first <see cref="SaveAsync"/> call
        /// (the initial checkpoint-creation write) succeeds instantly, but every subsequent call
        /// never completes on its own - simulating a genuinely stuck DB write - so it only ends
        /// when its caller's token is cancelled (here, by <see cref="WorkflowEngine"/>'s bounded
        /// persistence token, once <see cref="WorkflowEngine.PersistenceTimeout"/> elapses).
        /// </summary>
        private sealed class HangingWorkflowCheckpointStore : IWorkflowCheckpointStore
        {
            private int _saveCount;

            public Task SaveAsync(SessionCheckpointDocument document, CancellationToken ct = default) =>
                Interlocked.Increment(ref _saveCount) == 1
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.Infinite, ct);

            public Task<SessionCheckpointDocument?> LoadAsync(string sessionId, CancellationToken ct = default) =>
                Task.FromResult<SessionCheckpointDocument?>(null);

            public Task DeleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        }

        /// <summary>
        /// Simple in-memory <see cref="JsonCheckpointStore"/> fake for tests - stands in for the
        /// real <c>DbJsonCheckpointStore</c> (Command.Persistence, exercised separately against
        /// SQLite) so these engine-level tests don't need a real database or the file system.
        /// </summary>
        private sealed class InMemoryJsonCheckpointStore : JsonCheckpointStore, IJsonCheckpointStoreMaintenance
        {
            private sealed record Row(string CheckpointId, string? ParentCheckpointId, JsonElement Payload);

            private readonly Dictionary<string, List<Row>> _rowsBySession = new();

            public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent)
            {
                var checkpointId = Guid.NewGuid().ToString("N");
                if (!_rowsBySession.TryGetValue(sessionId, out var rows))
                    _rowsBySession[sessionId] = rows = [];

                rows.Add(new Row(checkpointId, parent?.CheckpointId, value.Clone()));
                return new ValueTask<CheckpointInfo>(new CheckpointInfo(sessionId, checkpointId));
            }

            public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo checkpoint)
            {
                var row = _rowsBySession.TryGetValue(sessionId, out var rows)
                    ? rows.FirstOrDefault(r => r.CheckpointId == checkpoint.CheckpointId)
                    : null;
                return row is null
                    ? throw new KeyNotFoundException($"No checkpoint '{checkpoint.CheckpointId}' for session '{sessionId}'.")
                    : new ValueTask<JsonElement>(row.Payload);
            }

            public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? parent)
            {
                var matches = _rowsBySession.TryGetValue(sessionId, out var rows)
                    ? rows.Where(r => r.ParentCheckpointId == parent?.CheckpointId).Select(r => new CheckpointInfo(sessionId, r.CheckpointId))
                    : [];
                return new ValueTask<IEnumerable<CheckpointInfo>>(matches);
            }

            public Task DeleteSessionCheckpointsAsync(string sessionId, CancellationToken ct = default)
            {
                _rowsBySession.Remove(sessionId);
                return Task.CompletedTask;
            }

            /// <summary>Test-only helper: how many checkpoint rows remain recorded for a session.</summary>
            public int CountFor(string sessionId) =>
                _rowsBySession.TryGetValue(sessionId, out var rows) ? rows.Count : 0;
        }

        private static WorkflowEngine CreateEngine(
            OrchestratorDefinition orchestrator,
            ISessionStore sessionStore,
            Dictionary<string, AIAgent> agentsByName,
            IWorkflowCheckpointStore? checkpointStore = null,
            JsonCheckpointStore? mafCheckpointStore = null,
            ILogger<WorkflowEngine>? logger = null)
        {
            var agentFactoryMock = new Mock<IAgentFactory>();
            agentFactoryMock
                .Setup(factory => factory.CreateAgentAsync(
                    It.IsAny<AgentDefinition>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((AgentDefinition agentDefinition, CancellationToken _) =>
                    agentsByName[agentDefinition.Name]);

            // Tests that need to inspect the persisted document (e.g. checking a file on disk)
            // provide their own store explicitly; tests that don't never touch the store, so any
            // instance suffices.
            var resolvedCheckpointStore = checkpointStore
                ?? new JsonFileWorkflowCheckpointStore(Path.Combine(Path.GetTempPath(), "workflow-engine-tests-unused"));

            return new WorkflowEngine(
                agentFactoryMock.Object,
                sessionStore,
                resolvedCheckpointStore,
                mafCheckpointStore ?? new InMemoryJsonCheckpointStore(),
                logger ?? NullLogger<WorkflowEngine>.Instance);
        }

        private static OrchestratorDefinition CreateOrchestrator(
            List<AgentDefinition> agents,
            HumanInLoopDefinition? humanInLoop = null,
            CheckpointingDefinition? checkpointing = null)
        {
            // HumanInLoopDefinition now lives nested inside CheckpointingDefinition (see
            // CheckpointingDefinition.HumanInLoop) - this helper keeps the two as separate
            // parameters for readability at call sites, merging them into the single nested
            // Checkpointing object the model actually exposes.
            var effectiveCheckpointing = checkpointing is not null
                ? new CheckpointingDefinition { Enabled = checkpointing.Enabled, HumanInLoop = humanInLoop }
                : humanInLoop is not null
                    ? new CheckpointingDefinition { HumanInLoop = humanInLoop }
                    : new CheckpointingDefinition();

            return new()
            {
                Id = "orch",
                Name = "Orchestrator",
                Pattern = "sequential",
                Agents = agents,
                Checkpointing = effectiveCheckpointing
            };
        }
    }
}
