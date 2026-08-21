using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Sessions;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// Executes sequential-pattern orchestrator workflows with optional durable checkpointing and
    /// human-in-the-loop review gates. Only the sequential pattern is supported - concurrent,
    /// handoff, and single-agent patterns were intentionally not migrated. Aura's arbitrary
    /// step-index rewind/replay "resume" capability, on the other hand, <em>is</em> supported -
    /// see <see cref="ResumeAsync"/>, which unifies answering a pending human-in-the-loop review,
    /// continuing after a crash/failure, and rewinding to an earlier step into a single
    /// checkpoint-id-driven operation.
    /// </summary>
    public sealed partial class WorkflowEngine : IWorkflowEngine
    {
        private const string StatusPendingApproval = "pending_approval";
        private const string StatusCompleted = "completed";
        private const string StatusFailed = "failed";
        private const string StatusRunning = "running";

        // Bounds checkpoint-persistence writes independently of the caller's own request
        // cancellation - see CreatePersistenceCts(). Internal (rather than a plain constant) only
        // so unit tests can shrink it to verify a genuinely stuck store still fails fast instead
        // of hanging, without waiting out the real production timeout.
        internal static TimeSpan PersistenceTimeout { get; set; } = TimeSpan.FromSeconds(30);

        private readonly IAgentFactory _agentFactory;
        private readonly ISessionStore _sessionStore;
        private readonly IWorkflowCheckpointStore _checkpointStore;
        private readonly JsonCheckpointStore _mafCheckpointStore;
        private readonly ILogger<WorkflowEngine> _logger;

        public WorkflowEngine(
            IAgentFactory agentFactory,
            ISessionStore sessionStore,
            IWorkflowCheckpointStore checkpointStore,
            JsonCheckpointStore mafCheckpointStore,
            ILogger<WorkflowEngine> logger)
        {
            _agentFactory = agentFactory;
            _sessionStore = sessionStore;
            _checkpointStore = checkpointStore;
            _mafCheckpointStore = mafCheckpointStore;
            _logger = logger;
        }

        public async Task<ExecuteResponse> ExecuteAsync(
            OrchestratorDefinition orchestrator,
            ExecuteRequest request,
            CancellationToken cancellationToken = default)
        {
            var session = _sessionStore.Create(orchestrator.Id);
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                var existing = _sessionStore.Get(request.SessionId);
                if (existing != null) session = existing;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Executing orchestrator '{OrchestratorId}' with pattern '{Pattern}', session '{SessionId}'",
                    orchestrator.Id, orchestrator.Pattern, session.SessionId);
            }

            if (orchestrator.Pattern != "sequential")
                throw new InvalidOperationException($"Unsupported pattern '{orchestrator.Pattern}'. This service only supports the 'sequential' pattern.");

            var checkpointCtx = CreateCheckpointContext(orchestrator, session, request.Input);
            if (checkpointCtx.Enabled)
                await checkpointCtx.Store!.SaveAsync(checkpointCtx.Document!, cancellationToken);

            try
            {
                var result = await ExecuteSequentialAsync(orchestrator, request.Input, session, checkpointCtx, cancellationToken);

                // Human-in-the-loop pauses mid-run (via a real MAF RequestPort, see
                // BuildReviewableSequentialWorkflow) - the pending-request fields are already
                // recorded on the session/checkpoint document by RunAgentWorkflowAsync at this
                // point, so there is nothing more to persist here.
                if (result.Paused)
                {
                    return new ExecuteResponse
                    {
                        SessionId = session.SessionId,
                        Status = StatusPendingApproval,
                        Output = session.PendingOutput,
                        Steps = session.Steps.ToList()
                    };
                }

                var output = result.Output ?? string.Empty;

                session.Status = StatusCompleted;
                session.CompletedAt = DateTime.UtcNow;
                session.Output = output;

                using (var persistCts = CreatePersistenceCts())
                    await FinalizeCheckpointAsync(checkpointCtx, session, persistCts.Token);
                return new ExecuteResponse
                {
                    SessionId = session.SessionId,
                    Status = StatusCompleted,
                    Output = output,
                    Steps = session.Steps.ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orchestrator '{OrchestratorId}' failed", orchestrator.Id);
                session.Status = StatusFailed;
                session.Error = ex.Message;
                session.CompletedAt = DateTime.UtcNow;

                using (var persistCts = CreatePersistenceCts())
                    await FinalizeCheckpointAsync(checkpointCtx, session, persistCts.Token);

                // The session/checkpoint above is intentionally still persisted as "failed" (so
                // GET session-status keeps working), but the failure itself is surfaced to the
                // caller via a classified exception instead of a 200 OK "failed" response - see
                // OrchestratorSessionsCommandController.Execute.
                throw new OrchestratorExecutionException(
                    session.SessionId, OrchestratorExecutionException.Classify(ex), ex.Message, ex);
            }
        }

        /// <summary>The result of running the sequential pattern: its final output, or an indication that it paused on a human-in-the-loop request instead.</summary>
        private sealed record PatternExecutionResult(string? Output, bool Paused);

        /// <summary>
        /// Runs each agent in a pipeline where one agent's output is the next agent's input. When
        /// the orchestrator has no human-in-the-loop configured, this uses a real MAF
        /// <see cref="Workflow"/> graph built via <see cref="SequentialWorkflowBuilder"/>. When
        /// human-in-the-loop *is* enabled, a manually-built "reviewable" graph is used instead (see
        /// <see cref="BuildReviewableSequentialWorkflow"/>), which inserts a MAF-native
        /// request/response gate after every agent.
        /// </summary>
        private async Task<PatternExecutionResult> ExecuteSequentialAsync(
            OrchestratorDefinition orchestrator, string input, OrchestratorSession session, CheckpointContext checkpointCtx, CancellationToken ct)
        {
            var agentDefs = orchestrator.Agents;
            var agents = await CreateAgentsAsync(agentDefs, ct);

            var humanInLoop = ResolveCheckpointing(orchestrator)?.HumanInLoop;
            var (workflow, reviewPortMeta) = BuildSequentialWorkflow(humanInLoop, agents, agentDefs);

            var runResult = await RunAgentWorkflowAsync(
                new WorkflowRunSpec(orchestrator, workflow, input, agents, agentDefs, CaptureTerminalOutput: true, reviewPortMeta),
                session, checkpointCtx, ct);

            // Each step was already durably persisted onto checkpointCtx.Document the moment it
            // completed (see PersistCompletedStepAsync) - session.Steps is the in-memory mirror.
            session.Steps.AddRange(runResult.Steps);

            return new PatternExecutionResult(runResult.FinalOutput, runResult.Paused);
        }

        private async Task<List<AIAgent>> CreateAgentsAsync(List<AgentDefinition> agentDefs, CancellationToken ct)
        {
            var agents = new List<AIAgent>(agentDefs.Count);
            foreach (var agentDef in agentDefs)
            {
                agents.Add(await _agentFactory.CreateAgentAsync(agentDef, ct));
            }

            return agents;
        }

        /// <summary>Groups the invariant inputs to <see cref="RunAgentWorkflowAsync"/> so the method itself stays within the parameter-count limit.</summary>
        private sealed record WorkflowRunSpec(
            OrchestratorDefinition Orchestrator,
            Workflow Workflow,
            string Input,
            List<AIAgent> Agents,
            List<AgentDefinition> AgentDefs,
            bool CaptureTerminalOutput,
            IReadOnlyDictionary<string, (int StepIndex, string AgentName)>? ReviewPortMeta = null);

        /// <summary>
        /// Runs a built MAF <see cref="Workflow"/> to completion (or until it pauses on a
        /// human-in-the-loop request) via <see cref="InProcessExecution.RunStreamingAsync{TInput}"/>,
        /// correlating the resulting event stream back to per-agent <see cref="AgentStepResult"/>s.
        /// </summary>
        /// <param name="spec">
        /// The workflow, input, agents/definitions, and human-in-the-loop review-port metadata for
        /// this run - see <see cref="WorkflowRunSpec"/>. <see cref="WorkflowRunSpec.ReviewPortMeta"/>
        /// maps each per-step review RequestPort id to its (step index, agent name) when the
        /// workflow was built by <see cref="BuildReviewableSequentialWorkflow"/>; a
        /// <see cref="RequestInfoEvent"/> raised on one of these ports pauses the run instead of
        /// being treated as an error. Null for orchestrators without human-in-the-loop.
        /// </param>
        private async Task<(string? FinalOutput, List<AgentStepResult> Steps, bool Paused)> RunAgentWorkflowAsync(
            WorkflowRunSpec spec,
            OrchestratorSession session,
            CheckpointContext checkpointCtx,
            CancellationToken ct)
        {
            var (orchestrator, workflow, input, agents, agentDefs, captureTerminalOutput, reviewPortMeta) = spec;
            var humanInLoopApprovalPrompt = ResolveCheckpointing(orchestrator)?.HumanInLoop?.ApprovalPrompt;

            var execIdToName = new Dictionary<string, string>();
            for (var i = 0; i < agents.Count; i++)
            {
                execIdToName[ComputeExecutorId(agents[i])] = agentDefs[i].Name;
            }

            var initialMessages = new List<ChatMessage> { new(ChatRole.User, input) };

            // When checkpointing is enabled, wire MAF's own documented checkpoint API
            // (CheckpointManager + the injected JsonCheckpointStore) as the graph-level
            // durability substrate for this run. This gives genuine MAF-native resume-as-is
            // capability (used by the human-in-the-loop pause/resume path below); it is
            // independent of - and does not replace - the step-level JSON manifest that drives
            // checkpoint listing.
            StreamingRun run;
            if (checkpointCtx.Enabled)
            {
                var checkpointManager = CheckpointManager.CreateJson(_mafCheckpointStore, new JsonSerializerOptions());
                run = await InProcessExecution.RunStreamingAsync(workflow, initialMessages, checkpointManager, session.SessionId, ct);
            }
            else
            {
                run = await InProcessExecution.RunStreamingAsync(workflow, initialMessages, cancellationToken: ct);
            }

            // A TurnToken must be sent explicitly to trigger the agents to actually process the
            // pending input and produce output.
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            var result = await ConsumeRunAsync(
                new ConsumeRunSpec(orchestrator, run, execIdToName, captureTerminalOutput, checkpointCtx, reviewPortMeta), ct);

            if (result.Paused)
            {
                ApplyPendingReview(session, checkpointCtx, humanInLoopApprovalPrompt, result);

                if (!checkpointCtx.Enabled)
                {
                    // ConfigValidator requires checkpointing to be enabled whenever
                    // humanInLoop is enabled (there is no other durable resume mechanism), so
                    // this should be unreachable in practice - guard against it explicitly
                    // rather than silently losing the paused run.
                    throw new InvalidOperationException(
                        $"Session '{session.SessionId}' paused for human-in-the-loop review, but checkpointing is not enabled for orchestrator '{orchestrator.Id}'. " +
                        "Enable checkpointing to support resuming a paused session.");
                }

                // The pending request (and the MAF checkpoint that captured it) is now
                // durably persisted, so the run itself can be disposed; ResumeAsync will
                // rehydrate it later via InProcessExecution.ResumeStreamingAsync.
                using (var persistCts = CreatePersistenceCts())
                    await checkpointCtx.Store!.SaveAsync(checkpointCtx.Document!, persistCts.Token);
                await run.DisposeAsync();

                return (null, result.Steps, true);
            }

            await run.DisposeAsync();
            return (result.FinalOutput, result.Steps, false);
        }

        /// <summary>
        /// Copies a paused run's pending human-in-the-loop request fields onto the in-memory
        /// <see cref="OrchestratorSession"/> and (when checkpointing is enabled) the durable
        /// <see cref="SessionCheckpointDocument"/>, so a later <see cref="ResumeAsync"/> call can
        /// re-answer it.
        /// </summary>
        private static void ApplyPendingReview(OrchestratorSession session, CheckpointContext checkpointCtx, string? approvalPrompt, ConsumeResult result)
        {
            session.Status = StatusPendingApproval;
            session.PendingRequestPortId = result.PendingRequestPortId;
            session.PendingStepIndex = result.PendingStepIndex;
            session.PendingAgentName = result.PendingAgentName;
            session.PendingOutput = result.PendingOutput;
            session.PendingApprovalPrompt = approvalPrompt ?? "Please review the step output.";

            if (!checkpointCtx.Enabled) return;

            var document = checkpointCtx.Document!;
            document.Status = StatusPendingApproval;
            document.PendingRequestPortId = result.PendingRequestPortId;
            document.PendingStepIndex = result.PendingStepIndex;
            document.PendingAgentName = result.PendingAgentName;
            document.PendingOutput = result.PendingOutput;
            document.PendingApprovalPrompt = session.PendingApprovalPrompt;
        }

        /// <summary>
        /// Overwrites the previously-recorded step's output with the (possibly human-edited) value
        /// that was actually sent as the review response, so the session/checkpoint history
        /// reflects what the next agent actually consumed rather than the agent's original,
        /// unreviewed output.
        /// </summary>
        private static void ReconcilePendingStepOutput(OrchestratorSession session, CheckpointContext checkpointCtx, int pendingStepIndex, string value)
        {
            if (pendingStepIndex >= 0 && pendingStepIndex < session.Steps.Count)
                session.Steps[pendingStepIndex].Output = value;

            if (checkpointCtx.Enabled)
            {
                var document = checkpointCtx.Document!;
                if (pendingStepIndex >= 0 && pendingStepIndex < document.Steps.Count)
                    document.Steps[pendingStepIndex].Output = value;
            }
        }

        /// <summary>The result of draining a run: either its terminal output, or a paused human-in-the-loop request.</summary>
        private sealed record ConsumeResult(
            string? FinalOutput,
            List<AgentStepResult> Steps,
            bool Paused,
            string? PendingRequestPortId = null,
            int? PendingStepIndex = null,
            string? PendingAgentName = null,
            string? PendingOutput = null,
            ExternalRequest? PendingRequest = null);

        /// <summary>Groups the invariant inputs to <see cref="ConsumeRunAsync"/> so the method itself stays within the parameter-count limit.</summary>
        private sealed record ConsumeRunSpec(
            OrchestratorDefinition Orchestrator,
            StreamingRun Run,
            IReadOnlyDictionary<string, string> ExecIdToName,
            bool CaptureTerminalOutput,
            CheckpointContext CheckpointCtx,
            IReadOnlyDictionary<string, (int StepIndex, string AgentName)>? ReviewPortMeta = null);

        /// <summary>
        /// Drains a <see cref="StreamingRun"/>'s event stream to completion (or until it pauses on
        /// an unanswered human-in-the-loop request), correlating events back to per-agent
        /// <see cref="AgentStepResult"/>s, and (when checkpointing is enabled) capturing the run's
        /// latest MAF-native checkpoint info onto <see cref="CheckpointContext.Document"/>.
        /// </summary>
        /// <param name="spec">The orchestrator, run, executor/agent-name map, checkpoint context, and human-in-the-loop review-port metadata for this drain - see <see cref="ConsumeRunSpec"/>.</param>
        /// <param name="pendingAnswer">
        /// When resuming a run that has an outstanding request (rehydrated from a checkpoint, or a
        /// parked live run), the (port id, response value) to answer the *first* matching
        /// <see cref="RequestInfoEvent"/> with inline, before continuing to drain for the next
        /// pause or the terminal output. Any *subsequent* <see cref="RequestInfoEvent"/> (i.e. a
        /// new pause) is still treated as a fresh pause per <see cref="ConsumeRunSpec.ReviewPortMeta"/>.
        /// </param>
        /// <remarks>
        /// Ownership of the run is NOT taken here (it is not disposed) - callers decide whether to
        /// dispose it or keep it open.
        /// </remarks>
        private static async Task<ConsumeResult> ConsumeRunAsync(
            ConsumeRunSpec spec,
            CancellationToken ct,
            (string PortId, string Value)? pendingAnswer = null)
        {
            var (orchestrator, run, execIdToName, captureTerminalOutput, checkpointCtx, reviewPortMeta) = spec;

            var textByExecId = new Dictionary<string, StringBuilder>();
            var startElapsedByExecId = new Dictionary<string, TimeSpan>();
            var steps = new List<AgentStepResult>();
            var stopwatch = Stopwatch.StartNew();
            var finalOutput = string.Empty;
            var answered = pendingAnswer is null;

            // A step completing (ExecutorCompletedEvent) does not by itself mean MAF has taken a
            // new graph-level checkpoint yet - checkpoints are captured once per *superstep*
            // (SuperStepCompletedEvent), which for a plain sequential chain is streamed as a
            // separate, later event. Buffer the just-completed step here and flush it (append to
            // the document + persist + capture run.LastCheckpoint) only once that superstep's
            // completion event is actually observed, so CheckpointId always reflects a checkpoint
            // that genuinely includes this step.
            AgentStepResult? pendingStepToPersist = null;

            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                switch (evt)
                {
                    case WorkflowErrorEvent errorEvent:
                        throw new InvalidOperationException(
                            $"Workflow for orchestrator '{orchestrator.Id}' failed.", errorEvent.Exception);

                    case ExecutorInvokedEvent invoked when execIdToName.ContainsKey(invoked.ExecutorId):
                        startElapsedByExecId[invoked.ExecutorId] = stopwatch.Elapsed;
                        break;

                    case AgentResponseUpdateEvent updateEvent when execIdToName.ContainsKey(updateEvent.ExecutorId):
                        if (!textByExecId.TryGetValue(updateEvent.ExecutorId, out var textBuilder))
                        {
                            textBuilder = new StringBuilder();
                            textByExecId[updateEvent.ExecutorId] = textBuilder;
                        }
                        textBuilder.Append(updateEvent.Update.Text);
                        break;

                    case ExecutorCompletedEvent completedEvent
                        when execIdToName.TryGetValue(completedEvent.ExecutorId, out var agentName)
                             && textByExecId.TryGetValue(completedEvent.ExecutorId, out var completedText):
                        pendingStepToPersist = RecordCompletedStep(steps, startElapsedByExecId, stopwatch, completedEvent.ExecutorId, agentName, completedText);
                        textByExecId.Remove(completedEvent.ExecutorId);
                        break;

                    case SuperStepCompletedEvent when pendingStepToPersist is { } stepToPersist:
                        // Persisted after every completed step's superstep (not just at pause/end)
                        // so a subsequent step's failure - or the process crashing before the run
                        // ever reaches completion - still leaves the last-good checkpoint (and
                        // this step's output) durably saved, making a "running"/"failed"
                        // checkpoint document resumable from here.
                        await PersistCompletedStepAsync(checkpointCtx, run, stepToPersist);
                        pendingStepToPersist = null;
                        break;

                    case RequestInfoEvent requestEvent when !answered && pendingAnswer is { } pa && requestEvent.Request.PortInfo.PortId == pa.PortId:
                        answered = true;
                        await run.SendResponseAsync(requestEvent.Request.CreateResponse(pa.Value));
                        break;

                    case RequestInfoEvent requestEvent when reviewPortMeta is not null
                        && reviewPortMeta.TryGetValue(requestEvent.Request.PortInfo.PortId, out var meta):
                        return BuildPauseResult(steps, checkpointCtx, run, requestEvent, meta);

                    case WorkflowOutputEvent outputEvent
                        when captureTerminalOutput
                             && !outputEvent.HasTag(OutputTag.Intermediate)
                             && outputEvent.Is<List<ChatMessage>>(out var messages):
                        finalOutput = ExtractFinalAssistantText(messages);
                        break;
                }
            }

            // StreamingRun.WatchStreamAsync(ct) ends its event stream silently on cancellation -
            // it does NOT cancel the underlying workflow execution, and does NOT throw. Left
            // unchecked, that makes the loop above exit exactly as it would on a genuine terminal
            // WorkflowOutputEvent, and callers (FinishResumeRunAsync / ExecuteAsync) would then
            // wrongly persist this as a completed run. Callers of this method are expected to run
            // against a token whose cancellation always represents a genuine interruption (see
            // WorkflowExecutionCoordinator, which deliberately does not forward the caller's own
            // HTTP request token here) - so treat that as the exceptional case it is, rather than
            // silently reporting completion.
            ct.ThrowIfCancellationRequested();

            // Fallback: flush a step whose SuperStepCompletedEvent (for whatever reason) never
            // arrived separately before the stream ended, so it is not silently dropped.
            if (pendingStepToPersist is { } lastPendingStep)
            {
                await PersistCompletedStepAsync(checkpointCtx, run, lastPendingStep);
            }

            if (checkpointCtx.Enabled && run.LastCheckpoint is { } lastCheckpoint)
            {
                checkpointCtx.Document!.CheckpointId = lastCheckpoint.CheckpointId;
            }

            return new ConsumeResult(finalOutput, steps, Paused: false);
        }

        /// <summary>
        /// <summary>
        /// Captures the run's latest MAF-native checkpoint id onto <see cref="CheckpointContext.Document"/>,
        /// when checkpointing is enabled and a checkpoint is available.
        /// </summary>
        private static void CaptureLastCheckpoint(CheckpointContext checkpointCtx, StreamingRun run)
        {
            if (checkpointCtx.Enabled && run.LastCheckpoint is { } checkpoint)
            {
                checkpointCtx.Document!.CheckpointId = checkpoint.CheckpointId;
            }
        }

        private static AgentStepResult RecordCompletedStep(
            List<AgentStepResult> steps,
            Dictionary<string, TimeSpan> startElapsedByExecId,
            Stopwatch stopwatch,
            string executorId,
            string agentName,
            StringBuilder completedText)
        {
            var durationMs = startElapsedByExecId.TryGetValue(executorId, out var startElapsed)
                ? (stopwatch.Elapsed - startElapsed).TotalMilliseconds
                : 0d;
            var step = new AgentStepResult
            {
                AgentName = agentName,
                Status = StatusCompleted,
                Output = completedText.ToString(),
                DurationMs = durationMs
            };
            steps.Add(step);
            return step;
        }

        /// <summary>
        /// Durably persists a single just-completed step (and the run's latest MAF-native
        /// checkpoint id) the moment it completes, rather than batching the whole run's steps into
        /// one write at the end. This is what makes a "running" checkpoint document (one whose run
        /// is later interrupted by a crash or a subsequent step's failure) resumable from the last
        /// completed step instead of only from the start of the run. No-op when checkpointing is
        /// disabled.
        /// </summary>
        private static async Task PersistCompletedStepAsync(CheckpointContext checkpointCtx, StreamingRun run, AgentStepResult step)
        {
            if (!checkpointCtx.Enabled) return;

            CaptureLastCheckpoint(checkpointCtx, run);

            var document = checkpointCtx.Document!;
            document.Steps.Add(new StepCheckpointRecord
            {
                StepIndex = document.Steps.Count,
                AgentName = step.AgentName,
                Status = step.Status,
                Output = step.Output,
                DurationMs = step.DurationMs,
                RecordedAt = DateTime.UtcNow,
                // Same checkpoint id just captured onto document.CheckpointId above - recording it
                // per-step too is what lets ResumeAsync target a rewind back to this exact step
                // later, even after later steps' checkpoints have superseded it as "the latest".
                CheckpointId = document.CheckpointId
            });

            // Bounded independently of the caller's own request cancellation (see
            // CreatePersistenceCts) - this runs mid-stream inline with every completed step, so it
            // must not be able to hang indefinitely on a stuck store while draining the run.
            using var persistCts = CreatePersistenceCts();
            await checkpointCtx.Store!.SaveAsync(document, persistCts.Token);
        }

        private static ConsumeResult BuildPauseResult(
            List<AgentStepResult> steps,
            CheckpointContext checkpointCtx,
            StreamingRun run,
            RequestInfoEvent requestEvent,
            (int StepIndex, string AgentName) meta)
        {
            CaptureLastCheckpoint(checkpointCtx, run);

            var reviewRequest = requestEvent.Request.TryGetDataAs<StepReviewRequest>(out var stepReview) ? stepReview : null;
            return new ConsumeResult(
                FinalOutput: null,
                Steps: steps,
                Paused: true,
                PendingRequestPortId: requestEvent.Request.PortInfo.PortId,
                PendingStepIndex: meta.StepIndex,
                PendingAgentName: meta.AgentName,
                PendingOutput: reviewRequest?.Output ?? string.Empty,
                PendingRequest: requestEvent.Request);
        }

        /// <summary>
        /// Builds the sequential workflow graph for an orchestrator run: the plain
        /// <see cref="SequentialWorkflowBuilder"/> graph when human-in-the-loop is not enabled, or
        /// the manually-built "reviewable" graph (see <see cref="BuildReviewableSequentialWorkflow"/>)
        /// when it is. Shared by both a fresh run (<see cref="ExecuteSequentialAsync"/>) and a
        /// running/failed continuation resume (<see cref="ResumeInterruptedAsync"/>), since MAF
        /// resumes a checkpoint against the same graph shape it was captured from.
        /// </summary>
        private static (Workflow Workflow, Dictionary<string, (int StepIndex, string AgentName)>? PortMeta) BuildSequentialWorkflow(
            HumanInLoopDefinition? humanInLoop, List<AIAgent> agents, List<AgentDefinition> agentDefs)
        {
            if (humanInLoop?.Enabled == true)
                return BuildReviewableSequentialWorkflow(humanInLoop.ApprovalPrompt, agents, agentDefs);

            // WithChainOnlyAgentResponses(true) => each agent receives only the previous agent's
            // output (not the full accumulated conversation).
            var workflow = new SequentialWorkflowBuilder(agents)
                .WithChainOnlyAgentResponses(true)
                .Build();
            return (workflow, null);
        }

        /// <summary>
        /// Builds a manual MAF <see cref="Workflow"/> graph equivalent to
        /// <see cref="SequentialWorkflowBuilder"/> but with a per-step, MAF-native
        /// human-in-the-loop request/response gate (<see cref="StepReviewGateExecutor"/> -&gt;
        /// RequestPort -&gt; <see cref="StepReviewCompleteExecutor"/>) inserted after every agent.
        /// Each step gets its own dedicated port (rather than one shared port) so MAF's per-node
        /// edge routing cannot fan a response out to more than one step's completion executor.
        /// </summary>
        private static (Workflow Workflow, Dictionary<string, (int StepIndex, string AgentName)> PortMeta) BuildReviewableSequentialWorkflow(
            string? approvalPrompt, List<AIAgent> agents, List<AgentDefinition> agentDefs)
        {
            var portMeta = new Dictionary<string, (int StepIndex, string AgentName)>();

            var builder = new WorkflowBuilder(agents[0]);
            for (var i = 0; i < agents.Count; i++)
            {
                var portId = ReviewPortId(i);
                var gate = new StepReviewGateExecutor($"review-gate-{i}", i, agentDefs[i].Name, approvalPrompt);
                var port = RequestPort.Create<StepReviewRequest, string>(portId);
                var isTerminalStep = i + 1 >= agents.Count;
                var complete = new StepReviewCompleteExecutor($"review-complete-{i}", isTerminalStep);

                builder = builder
                    .AddEdge(agents[i], gate)
                    .AddEdge(gate, port)
                    .AddEdge(port, complete);

                portMeta[portId] = (i, agentDefs[i].Name);

                builder = i + 1 < agents.Count
                    ? builder.AddEdge(complete, agents[i + 1])
                    : builder.WithOutputFrom(complete);
            }

            return (builder.Build(), portMeta);
        }

        /// <summary>
        /// The deterministic MAF request-port id for step <paramref name="stepIndex"/>'s
        /// human-in-the-loop review gate, as wired up by
        /// <see cref="BuildReviewableSequentialWorkflow"/>. Shared with
        /// <see cref="ResumeFromCheckpointAsync"/> so it can construct the same port id purely
        /// from a step index, without needing a live run, to "fast-forward" a rewind past an
        /// already-resolved gate.
        /// </summary>
        private static string ReviewPortId(int stepIndex) => $"step-review-{stepIndex}";


        /// <summary>
        /// Resumes a session's workflow from a durable checkpoint. Dispatches purely on
        /// <see cref="ResumeRequest.Action"/> - no inference from checkpoint-id equality or which
        /// optional fields happen to be set:
        /// <list type="bullet">
        /// <item><description><see cref="ResumeAction.Continue"/>: answers the session's
        /// outstanding human-in-the-loop request - approving the recorded step output as-is, or
        /// substituting an edited version of it (see <see cref="AnswerPendingReviewAsync"/>).
        /// Requires the session to be <c>pending_approval</c>.</description></item>
        /// <item><description><see cref="ResumeAction.Reject"/>: abandons the session (see
        /// <see cref="RejectAsync"/>) - terminal, no further resume is possible. Requires the
        /// session to be <c>pending_approval</c>.</description></item>
        /// <item><description><see cref="ResumeAction.RedoFromStep"/>: resumes/rewinds from
        /// <see cref="ResumeRequest.StepIndex"/> (see <see cref="ResumeFromCheckpointAsync"/>) -
        /// continuing forward as-is when it names the session's own last completed step (e.g.
        /// resuming a crashed/failed run), or truncating and re-executing from that step onward
        /// (discarding its and every later step's prior output) otherwise. Works from any session
        /// status, and <see cref="ResumeRequest.StepIndex"/> may name either an already-completed
        /// step or the step currently awaiting review - both are redone identically, since this
        /// action never routes through <see cref="AnswerPendingReviewAsync"/>. When
        /// <see cref="ResumeRequest.StepIndex"/> is greater than <c>0</c> and names an
        /// already-completed step under human-in-the-loop, <see cref="ResumeRequest.EditedOutput"/>
        /// may also override the immediately-preceding step's recorded output before it's replayed
        /// forward.</description></item>
        /// </list>
        /// Checkpointing must be enabled for the orchestrator; there is no non-checkpointed
        /// ("live", in-memory-only) resume path. If the in-memory session was lost (e.g. the
        /// process restarted since the run started), it is rehydrated from the checkpoint document
        /// rather than failing with "session not found" - this is what makes resume genuinely
        /// crash-durable rather than depending on the process never having restarted.
        /// </summary>
        public async Task<ExecuteResponse> ResumeAsync(
            OrchestratorDefinition orchestrator, string sessionId, ResumeRequest request, CancellationToken cancellationToken = default)
        {
            var checkpointing = ResolveCheckpointing(orchestrator);
            if (checkpointing is not { Enabled: true })
                throw new InvalidOperationException($"Checkpointing is not enabled for orchestrator '{orchestrator.Id}'; resume requires checkpointing.");

            // Looked up (not rehydrated yet) before the try block so a failure while loading the
            // checkpoint document below can still be recorded against an already-in-memory
            // session - see the catch block.
            var session = _sessionStore.Get(sessionId);

            try
            {
                var document = await _checkpointStore.LoadAsync(sessionId, cancellationToken)
                    ?? throw new InvalidOperationException($"No checkpoint found for session '{sessionId}'.");

                session ??= RehydrateSession(orchestrator.Id, document);

                switch (request.Action)
                {
                    case ResumeAction.Reject:
                        if (document.Status != StatusPendingApproval)
                            throw new InvalidOperationException(
                                $"Session '{sessionId}' is not pending approval (status: '{document.Status}'); nothing to reject.");
                        return await RejectAsync(session, sessionId);

                    case ResumeAction.Continue:
                        if (document.Status != StatusPendingApproval)
                            throw new InvalidOperationException(
                                $"Session '{sessionId}' is not pending approval (status: '{document.Status}'); nothing to continue.");
                        return await AnswerPendingReviewAsync(orchestrator, session, checkpointing, request, cancellationToken);

                    case ResumeAction.RedoFromStep:
                        if (request.StepIndex is not { } stepIndex)
                            throw new InvalidOperationException("StepIndex is required when Action is RedoFromStep.");
                        ValidateStepIndex(document, stepIndex);
                        if (document.Status == StatusPendingApproval && stepIndex == document.Steps.Count)
                            throw new InvalidOperationException(
                                $"Session '{sessionId}' is pending approval; step {stepIndex} has not executed yet. " +
                                "Use Action.Continue (optionally with EditedOutput) to answer the pending review instead of RedoFromStep.");
                        return await ResumeFromCheckpointAsync(
                            orchestrator, session, document, stepIndex, request.EditedOutput, checkpointing, cancellationToken);

                    default:
                        throw new InvalidOperationException($"Unrecognized resume action '{request.Action}'.");
                }
            }
            catch (InvalidOperationException)
            {
                // Rethrown as-is: these signal a bad resume request (no such session/checkpoint,
                // wrong state) rather than an execution failure, and are already mapped to 400
                // Bad Request by OrchestratorSessionsCommandController.Resume.
                throw;
            }
            catch (Exception ex)
            {
                if (session is null)
                {
                    // The checkpoint document couldn't even be loaded, and there was no in-memory
                    // session to rehydrate from or record the failure against (e.g. a genuinely
                    // unknown session, or a process restart with no prior in-memory state) -
                    // nothing durable to update, so just classify and rethrow.
                    throw new OrchestratorExecutionException(
                        sessionId, OrchestratorExecutionException.Classify(ex), ex.Message, ex);
                }

                await RecordResumeFailureAsync(orchestrator, session, ex, cancellationToken);

                throw new OrchestratorExecutionException(
                    session.SessionId, OrchestratorExecutionException.Classify(ex), ex.Message, ex);
            }
        }

        /// <summary>
        /// Rebuilds an in-memory <see cref="OrchestratorSession"/> from a durable checkpoint
        /// document when the process's own in-memory session for it is gone (e.g. after a
        /// restart), registering it with <see cref="_sessionStore"/> under the same session id so
        /// subsequent calls (e.g. session-status queries) see it too.
        /// </summary>
        private OrchestratorSession RehydrateSession(string orchestratorId, SessionCheckpointDocument document)
        {
            var session = _sessionStore.GetOrCreate(document.SessionId, orchestratorId);
            SyncStepsFromDocument(session, document);
            session.Status = document.Status;
            session.Output = document.FinalOutput;
            session.Error = document.Error;
            session.PendingRequestPortId = document.PendingRequestPortId;
            session.PendingStepIndex = document.PendingStepIndex;
            session.PendingAgentName = document.PendingAgentName;
            session.PendingOutput = document.PendingOutput;
            session.PendingApprovalPrompt = document.PendingApprovalPrompt;
            return session;
        }

        /// <summary>
        /// Logs and durably records a resume failure. Distinguishes a caller-triggered
        /// cancellation (client disconnect, or an ingress/gateway timeout that fires while a
        /// long-running agent step is still in progress - not itself a bug) from a genuine
        /// unexpected exception, logging the former at <see cref="LogLevel.Warning"/> rather than
        /// <see cref="LogLevel.Error"/>. Either way, the failure is written to the checkpoint
        /// store using a bounded token independent of the (possibly already-cancelled) caller
        /// token - see <see cref="CreatePersistenceCts"/> - so the failure state is still
        /// recorded even when the caller has disconnected.
        /// </summary>
        private async Task RecordResumeFailureAsync(
            OrchestratorDefinition orchestrator, OrchestratorSession session, Exception ex, CancellationToken callerToken)
        {
            if (ex is OperationCanceledException && callerToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Orchestrator '{OrchestratorId}' resume for session '{SessionId}' was cancelled by the caller " +
                    "(client disconnect or gateway/ingress timeout) while a step was still in progress",
                    orchestrator.Id, session.SessionId);
            }
            else
            {
                _logger.LogError(ex, "Orchestrator '{OrchestratorId}' resume failed for session '{SessionId}'", orchestrator.Id, session.SessionId);
            }

            session.Status = StatusFailed;
            session.Error = ex.Message;
            session.CompletedAt = DateTime.UtcNow;

            using var persistCts = CreatePersistenceCts();
            var document = await _checkpointStore.LoadAsync(session.SessionId, persistCts.Token);
            if (document is null) return;

            document.Status = StatusFailed;
            document.Error = ex.Message;
            await _checkpointStore.SaveAsync(document, persistCts.Token);
        }

        private async Task<ExecuteResponse> RejectAsync(OrchestratorSession session, string sessionId)
        {
            session.Status = "rejected";
            session.CompletedAt = DateTime.UtcNow;

            using var persistCts = CreatePersistenceCts();
            var document = await _checkpointStore.LoadAsync(sessionId, persistCts.Token);
            if (document is not null)
            {
                document.Status = "rejected";
                await _checkpointStore.SaveAsync(document, persistCts.Token);
            }

            return new ExecuteResponse { SessionId = session.SessionId, Status = "rejected", Steps = session.Steps.ToList() };
        }

        /// <summary>
        /// Answers a pending human-in-the-loop request for a checkpointed orchestrator by
        /// rehydrating the exact MAF-native graph state via
        /// <see cref="InProcessExecution.ResumeStreamingAsync"/> (the checkpoint captured the
        /// pending request itself), re-answering the request MAF re-emits on rehydrate with the
        /// (possibly edited) output, and draining for the next pause or the terminal output.
        /// </summary>
        private async Task<ExecuteResponse> AnswerPendingReviewAsync(
            OrchestratorDefinition orchestrator, OrchestratorSession session, CheckpointingDefinition checkpointing, ResumeRequest request, CancellationToken ct)
        {
            var document = await _checkpointStore.LoadAsync(session.SessionId, ct)
                ?? throw new InvalidOperationException($"No checkpoint found for session '{session.SessionId}'.");

            if (string.IsNullOrEmpty(document.CheckpointId) || string.IsNullOrEmpty(document.PendingRequestPortId))
                throw new InvalidOperationException("No MAF-native checkpoint was recorded for the pending human-in-the-loop request.");

            var checkpointCtx = new CheckpointContext(true, _checkpointStore, document);

            var agentDefs = orchestrator.Agents;
            var agents = await CreateAgentsAsync(agentDefs, ct);
            var approvalPrompt = checkpointing.HumanInLoop?.ApprovalPrompt;
            var (workflow, portMeta) = BuildReviewableSequentialWorkflow(approvalPrompt, agents, agentDefs);

            var execIdToName = new Dictionary<string, string>();
            for (var i = 0; i < agents.Count; i++)
                execIdToName[ComputeExecutorId(agents[i])] = agentDefs[i].Name;

            var checkpointManager = CheckpointManager.CreateJson(_mafCheckpointStore, new JsonSerializerOptions());
            var checkpointInfo = new CheckpointInfo(document.SessionId, document.CheckpointId);
            var run = await InProcessExecution.ResumeStreamingAsync(workflow, checkpointInfo, checkpointManager, ct);

            var value = !string.IsNullOrEmpty(request.EditedOutput) ? request.EditedOutput : document.PendingOutput ?? string.Empty;
            ReconcilePendingStepOutput(session, checkpointCtx, document.PendingStepIndex ?? -1, value);
            var result = await ConsumeRunAsync(
                new ConsumeRunSpec(orchestrator, run, execIdToName, CaptureTerminalOutput: true, checkpointCtx, portMeta), ct,
                pendingAnswer: (document.PendingRequestPortId!, value));

            session.Steps.AddRange(result.Steps);

            return await FinishResumeRunAsync(session, checkpointCtx, checkpointing, result, run);
        }

        /// <summary>
        /// Validates that <paramref name="stepIndex"/> is in range for <paramref name="document"/>
        /// (0 through <see cref="SessionCheckpointDocument.Steps"/>.Count inclusive - the latter
        /// meaning "resume forward from the last completed step, nothing to truncate"). Throws
        /// <see cref="InvalidOperationException"/> otherwise.
        /// </summary>
        private static void ValidateStepIndex(SessionCheckpointDocument document, int stepIndex)
        {
            if (stepIndex < 0 || stepIndex > document.Steps.Count)
                throw new InvalidOperationException(
                    $"Step index {stepIndex} is out of range for session '{document.SessionId}' " +
                    $"(it has {document.Steps.Count} recorded step(s)).");
        }

        /// <summary>
        /// Resumes - or rewinds - a checkpointed run from an arbitrary step, identified directly by
        /// <paramref name="stepIndex"/> (0-based index into
        /// <see cref="SessionCheckpointDocument.Steps"/>, already validated by
        /// <see cref="ValidateStepIndex"/>). Unifies what were previously two separate operations:
        /// <list type="bullet">
        /// <item><description><b>Plain continue</b> (<paramref name="stepIndex"/> equals
        /// <see cref="SessionCheckpointDocument.Steps"/>.Count): continues a run that was
        /// interrupted - either a process crash mid-run (the document is left at
        /// <see cref="StatusRunning"/>) or a step that threw (<see cref="StatusFailed"/>) - from
        /// its last completed step's MAF-native checkpoint. Nothing is truncated.</description></item>
        /// <item><description><b>Rewind</b> (an earlier, already-completed step, or the step
        /// currently awaiting human-in-the-loop review): truncates
        /// <see cref="SessionCheckpointDocument.Steps"/> back to that step (discarding its and every
        /// later step's prior output) and re-executes forward from there. Rewinding to the very
        /// first step (index 0) falls back to a fresh <see cref="InProcessExecution.RunStreamingAsync{TInput}"/>
        /// run instead of a checkpoint resume, since MAF captures no checkpoint before the first
        /// agent runs; <paramref name="inputOverride"/> (if provided) replaces the session's
        /// original input in that case only. Rewinding a step beyond the first, under
        /// human-in-the-loop, instead lets <paramref name="inputOverride"/> override the
        /// immediately-preceding step's (<c>stepIndex - 1</c>) recorded output - the value that
        /// gets replayed forward as that step's already-resolved review answer (see below) - since
        /// only the first step's input is the session's own input rather than a prior step's
        /// output. Supplying <paramref name="inputOverride"/> for a plain continue, or when
        /// human-in-the-loop is disabled, throws: there is no mechanism to carry the override into
        /// the resumed run in either case.</description></item>
        /// </list>
        /// Either way this never routes through <see cref="AnswerPendingReviewAsync"/> to answer
        /// the *target* step's own review, regardless of whether that step's checkpoint id happens
        /// to equal <see cref="SessionCheckpointDocument.CheckpointId"/> - this is what makes
        /// rewinding the step currently pending review work correctly. A true rewind past step 0
        /// under human-in-the-loop does, however, pass one <c>pendingAnswer</c> to
        /// <see cref="ConsumeRunAsync"/>: a replay of the immediately-preceding step's own
        /// already-resolved review (its recorded output, possibly just overridden by
        /// <paramref name="inputOverride"/> - see above), needed purely to fast-forward through
        /// that already-answered gate - not to answer <paramref name="stepIndex"/>'s own review,
        /// which is never pre-answered here.
        /// </summary>
        private async Task<ExecuteResponse> ResumeFromCheckpointAsync(
            OrchestratorDefinition orchestrator, OrchestratorSession session, SessionCheckpointDocument document,
            int stepIndex, string? inputOverride, CheckpointingDefinition checkpointing, CancellationToken ct)
        {
            // Captured before truncation below - RemoveRange always shrinks document.Steps.Count
            // down to exactly stepIndex whenever a genuine rewind happens, so comparing stepIndex
            // against the (post-truncation) document.Steps.Count later would always read as "plain
            // continue" and never take the true-rewind branch. Compare against this instead.
            var originalStepCount = document.Steps.Count;

            // Computed here (rather than only in the stepIndex > 0 execution branch below) so it
            // can also gate the EditedOutput-overrides-the-prior-step's-output validation
            // immediately below, before any mutation of document.Steps happens.
            var isRewind = stepIndex < originalStepCount;

            if (stepIndex > 0 && !string.IsNullOrEmpty(inputOverride))
            {
                // EditedOutput's "override the prior step's output" meaning only makes sense for a
                // genuine rewind (there is no "prior step" concept to edit when plainly continuing
                // a crashed/failed run forward from where it left off), and only human-in-the-loop
                // graphs have a review-gate replay mechanism (pendingAnswer, below) capable of
                // actually carrying the override into the resumed run.
                if (!isRewind || checkpointing.HumanInLoop?.Enabled != true)
                    throw new InvalidOperationException(
                        $"EditedOutput can only override the immediately-preceding step's output when redoing " +
                        $"an already-completed step under an orchestrator with human-in-the-loop enabled. " +
                        $"Session '{session.SessionId}' step {stepIndex} is a plain continue and/or " +
                        "human-in-the-loop is disabled, so there is no mechanism to carry the override forward.");

                // Persisted (with everything else) below, before the run starts, so this edit is
                // durable even if the process crashes right after this point. Reading it back out
                // of document.Steps[stepIndex - 1] further down (for the pendingAnswer replay)
                // therefore naturally picks up this override with no further changes needed there.
                document.Steps[stepIndex - 1].Output = inputOverride;
            }

            // Discard the rewound-past steps' prior outputs - a no-op for the plain-continue case,
            // where stepIndex already equals document.Steps.Count.
            if (stepIndex < originalStepCount)
                document.Steps.RemoveRange(stepIndex, originalStepCount - stepIndex);

            // Rewinding out of pending_approval/failed/completed must clear all stale
            // pending-review/error state, not just flip Status back to running.
            session.Status = StatusRunning;
            session.Error = null;
            session.PendingRequestPortId = null;
            session.PendingStepIndex = null;
            session.PendingAgentName = null;
            session.PendingOutput = null;
            session.PendingApprovalPrompt = null;

            document.Status = StatusRunning;
            document.Error = null;
            document.PendingRequestPortId = null;
            document.PendingStepIndex = null;
            document.PendingAgentName = null;
            document.PendingOutput = null;
            document.PendingApprovalPrompt = null;

            SyncStepsFromDocument(session, document);

            var checkpointCtx = new CheckpointContext(true, _checkpointStore, document);

            // Persisted before the run starts so a crash mid-rewind doesn't leave stale
            // post-rewind steps lingering in the durable document.
            using (var persistCts = CreatePersistenceCts())
                await checkpointCtx.Store!.SaveAsync(document, persistCts.Token);

            var agentDefs = orchestrator.Agents;
            var agents = await CreateAgentsAsync(agentDefs, ct);
            var (workflow, portMeta) = BuildSequentialWorkflow(checkpointing.HumanInLoop, agents, agentDefs);

            var execIdToName = new Dictionary<string, string>();
            for (var i = 0; i < agents.Count; i++)
                execIdToName[ComputeExecutorId(agents[i])] = agentDefs[i].Name;

            var checkpointManager = CheckpointManager.CreateJson(_mafCheckpointStore, new JsonSerializerOptions());
            var startOrResumeSpec = new StartOrResumeRunSpec(workflow, document, stepIndex, isRewind, inputOverride, checkpointing);
            var (run, pendingAnswer) = await StartOrResumeRunAsync(startOrResumeSpec, checkpointManager, ct);

            var result = await ConsumeRunAsync(
                new ConsumeRunSpec(orchestrator, run, execIdToName, CaptureTerminalOutput: true, checkpointCtx, portMeta), ct,
                pendingAnswer: pendingAnswer);

            // The newly-produced steps were already durably appended onto document.Steps (and
            // hence checkpointCtx.Document, the same instance) as they completed - see
            // PersistCompletedStepAsync - so session.Steps is re-synced from the document
            // rather than double-appending result.Steps.
            SyncStepsFromDocument(session, document);

            return await FinishResumeRunAsync(session, checkpointCtx, checkpointing, result, run);
        }

        /// <summary>Groups the invariant inputs to <see cref="StartOrResumeRunAsync"/> so the method itself stays within the parameter-count limit.</summary>
        private sealed record StartOrResumeRunSpec(
            Workflow Workflow,
            SessionCheckpointDocument Document,
            int StepIndex,
            bool IsRewind,
            string? InputOverride,
            CheckpointingDefinition Checkpointing);

        /// <summary>
        /// Starts a fresh workflow run (<paramref name="spec"/>'s <c>StepIndex</c> == 0) or resumes
        /// one from a MAF checkpoint (<c>StepIndex</c> > 0), on behalf of
        /// <see cref="ResumeFromCheckpointAsync"/> - extracted purely to keep that method's
        /// cognitive complexity within bounds; see <see cref="ResumeFromCheckpointAsync"/> for the
        /// wider rewind/resume semantics this implements.
        /// </summary>
        private static async Task<(StreamingRun Run, (string PortId, string Value)? PendingAnswer)> StartOrResumeRunAsync(
            StartOrResumeRunSpec spec, CheckpointManager checkpointManager, CancellationToken ct)
        {
            var (workflow, document, stepIndex, isRewind, inputOverride, checkpointing) = spec;
            var sessionId = document.SessionId;

            if (stepIndex == 0)
            {
                // No MAF checkpoint exists before the very first agent runs - fall back to a fresh
                // run, optionally overriding the session's original input.
                var input = !string.IsNullOrEmpty(inputOverride) ? inputOverride : document.Input;
                var initialMessages = new List<ChatMessage> { new(ChatRole.User, input) };
                var freshRun = await InProcessExecution.RunStreamingAsync(workflow, initialMessages, checkpointManager, sessionId, ct);
                await freshRun.TrySendMessageAsync(new TurnToken(emitEvents: true));
                return (freshRun, null);
            }

            // "Plain continue" (stepIndex == originalStepCount) resumes from the session's own
            // last checkpoint - the step that step genuinely never reached its own review gate
            // (interrupted by a crash/failure), so it's correct to let it pause there if
            // humanInLoop is enabled. A true rewind (stepIndex < originalStepCount, computed
            // as isRewind above) instead resumes from the checkpoint captured right after the
            // PRIOR step (stepIndex - 1) completed, since MAF resumes forward, re-executing the
            // *next* executor after the one that captured the checkpoint.
            var resumeFromCheckpointId = isRewind
                ? document.Steps[stepIndex - 1].CheckpointId
                : document.CheckpointId;

            if (string.IsNullOrEmpty(resumeFromCheckpointId))
                throw new InvalidOperationException(
                    $"No MAF checkpoint is available to resume session '{sessionId}' from step {stepIndex}.");

            // When humanInLoop is enabled, that "next executor after the checkpoint" is the
            // PRIOR step's own review gate - not stepIndex's agent directly - because every
            // step's checkpoint is captured the instant its agent finishes, before its own gate
            // (see PersistCompletedStepAsync). That prior gate was already answered/approved in
            // the original run, so it must be fast-forwarded through here by replaying its
            // already-recorded (possibly edited) answer - otherwise the resumed run would just
            // re-pause on that already-resolved gate instead of reaching stepIndex's agent.
            (string PortId, string Value)? pendingAnswer = null;
            if (isRewind && checkpointing.HumanInLoop?.Enabled == true)
            {
                var priorStep = document.Steps[stepIndex - 1];
                pendingAnswer = (ReviewPortId(stepIndex - 1), priorStep.Output ?? string.Empty);
            }

            var checkpointInfo = new CheckpointInfo(document.SessionId, resumeFromCheckpointId);
            var resumedRun = await InProcessExecution.ResumeStreamingAsync(workflow, checkpointInfo, checkpointManager, ct);
            return (resumedRun, pendingAnswer);
        }

        /// <summary>
        /// Shared tail for both <see cref="AnswerPendingReviewAsync"/> and
        /// <see cref="ResumeFromCheckpointAsync"/>: handles a drained run's pause-for-review or
        /// completion outcome identically - applying/persisting the pending review, or finalizing
        /// the checkpoint - and disposes the run either way.
        /// </summary>
        private static async Task<ExecuteResponse> FinishResumeRunAsync(
            OrchestratorSession session, CheckpointContext checkpointCtx, CheckpointingDefinition checkpointing, ConsumeResult result, StreamingRun run)
        {
            if (result.Paused)
            {
                var approvalPrompt = checkpointing.HumanInLoop?.ApprovalPrompt;
                ApplyPendingReview(session, checkpointCtx, approvalPrompt, result);
                using (var persistCts = CreatePersistenceCts())
                    await checkpointCtx.Store!.SaveAsync(checkpointCtx.Document!, persistCts.Token);
                await run.DisposeAsync();

                return new ExecuteResponse { SessionId = session.SessionId, Status = StatusPendingApproval, Output = session.PendingOutput, Steps = session.Steps.ToList() };
            }

            await run.DisposeAsync();

            session.Status = StatusCompleted;
            session.CompletedAt = DateTime.UtcNow;
            session.Output = result.FinalOutput;
            using (var persistCts = CreatePersistenceCts())
                await FinalizeCheckpointAsync(checkpointCtx, session, persistCts.Token);

            return new ExecuteResponse { SessionId = session.SessionId, Status = StatusCompleted, Output = result.FinalOutput, Steps = session.Steps.ToList() };
        }

        /// <summary>Rebuilds <paramref name="session"/>'s in-memory step list from the durable checkpoint document.</summary>
        private static void SyncStepsFromDocument(OrchestratorSession session, SessionCheckpointDocument document)
        {
            session.Steps.Clear();
            session.Steps.AddRange(document.Steps.Select(s => new AgentStepResult
            {
                AgentName = s.AgentName,
                Status = s.Status,
                Output = s.Output,
                DurationMs = s.DurationMs
            }));
        }

        /// <summary>
        /// Concatenates the non-empty text content of a forwarded workflow chat-message batch into
        /// a single string, mirroring <see cref="AgentRunResponse.Text"/>'s aggregation behavior.
        /// </summary>
        internal static string ExtractText(IEnumerable<ChatMessage> messages) =>
            string.Join(string.Empty, messages.Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));

        /// <summary>
        /// Returns the most recent non-empty assistant-authored message's text from a chat-message
        /// batch, falling back to <see cref="ExtractText"/> if no assistant message is found.
        /// </summary>
        internal static string ExtractFinalAssistantText(IReadOnlyList<ChatMessage> messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == ChatRole.Assistant && !string.IsNullOrEmpty(messages[i].Text))
                    return messages[i].Text!;
            }

            return ExtractText(messages);
        }

        /// <summary>
        /// Like <see cref="ExtractFinalAssistantText"/>, but returns null (rather than falling back
        /// to concatenating every message) when the batch contains no assistant-authored message.
        /// Used by <see cref="StepReviewGateExecutor"/> to distinguish an agent's own generated
        /// response from a forwarded-input batch it also relays.
        /// </summary>
        internal static string? ExtractFinalAssistantTextOrNull(IReadOnlyList<ChatMessage> messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role == ChatRole.Assistant && !string.IsNullOrEmpty(messages[i].Text))
                    return messages[i].Text!;
            }

            return null;
        }

        /// <summary>
        /// Computes the workflow ExecutorId MAF assigns to an agent-hosted executor:
        /// <c>sanitize("{agent.Name}_{agent.Id}")</c>, where sanitization replaces every run of
        /// non-alphanumeric characters with a single underscore.
        /// </summary>
        internal static string ComputeExecutorId(AIAgent agent)
        {
            var id = string.IsNullOrEmpty(agent.Name) ? agent.Id : $"{agent.Name}_{agent.Id}";
            return NonAlphanumericRunRegex().Replace(id, "_");
        }

        // A 1s match timeout bounds worst-case regex evaluation time (defense-in-depth against
        // catastrophic backtracking / ReDoS), even though this specific pattern is linear.
        [GeneratedRegex("[^0-9A-Za-z]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex NonAlphanumericRunRegex();

        // ---------------------------------------------------------------------------------------
        // Checkpointing
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Per-execution checkpointing state threaded through <see cref="ExecuteAsync"/> and
        /// <see cref="ExecuteSequentialAsync"/>. When <see cref="Enabled"/> is false, every
        /// checkpointing call site is a no-op, so disabled orchestrators pay effectively zero cost.
        /// </summary>
        private sealed class CheckpointContext(bool enabled, IWorkflowCheckpointStore? store, SessionCheckpointDocument? document)
        {
            public bool Enabled { get; } = enabled;
            public IWorkflowCheckpointStore? Store { get; } = store;
            public SessionCheckpointDocument? Document { get; set; } = document;

            public static readonly CheckpointContext Disabled = new(false, null, null);
        }

        /// <summary>Resolves the effective checkpointing config for an orchestrator (always non-null now).</summary>
        private static CheckpointingDefinition ResolveCheckpointing(OrchestratorDefinition orchestrator) =>
            orchestrator.Checkpointing;

        /// <summary>
        /// Creates a bounded <see cref="CancellationToken"/> source for checkpoint-persistence
        /// writes, deliberately independent of the caller's own request token. Agent calls are
        /// allowed to run for several minutes (see ChatClientFactory.DefaultNetworkTimeout), which
        /// can outlast the caller's own connection (browser/gateway/ingress timeout or a client
        /// disconnect); if persistence reused that same token, a cancellation landing right after
        /// the agent work finished - but before the result was saved - would silently discard
        /// already-completed work (see the OperationCanceledException surfaced from
        /// DbWorkflowCheckpointStore.SaveAsync when this happens). The token returned here still
        /// can't hang forever: it self-cancels after <see cref="PersistenceTimeout"/>, so a
        /// genuinely stuck write still fails instead of blocking indefinitely. Callers must
        /// dispose the returned source (e.g. via <c>using</c>).
        /// </summary>
        private static CancellationTokenSource CreatePersistenceCts() => new(PersistenceTimeout);

        private CheckpointContext CreateCheckpointContext(OrchestratorDefinition orchestrator, OrchestratorSession session, string input)
        {
            var config = ResolveCheckpointing(orchestrator);
            if (config is not { Enabled: true })
                return CheckpointContext.Disabled;

            var document = new SessionCheckpointDocument
            {
                SessionId = session.SessionId,
                OrchestratorId = orchestrator.Id,
                Pattern = orchestrator.Pattern,
                Input = input,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Status = StatusRunning
            };

            return new CheckpointContext(true, _checkpointStore, document);
        }

        /// <summary>Updates the checkpoint document's final status/output/error and persists it. No-op when checkpointing is disabled.</summary>
        private static async Task FinalizeCheckpointAsync(CheckpointContext ctx, OrchestratorSession session, CancellationToken ct)
        {
            if (!ctx.Enabled) return;

            var document = ctx.Document!;
            document.Status = session.Status;
            document.FinalOutput = session.Output;
            document.Error = session.Error;
            await ctx.Store!.SaveAsync(document, ct);
        }

        public async Task<SessionCheckpointDocument?> GetCheckpointsAsync(
            OrchestratorDefinition orchestrator, string sessionId, CancellationToken cancellationToken = default)
        {
            var config = ResolveCheckpointing(orchestrator);
            if (config is not { Enabled: true })
                return null;

            return await _checkpointStore.LoadAsync(sessionId, cancellationToken);
        }

        /// <summary>
        /// Deletes the durable checkpoint document for a session (requires checkpointing to be
        /// enabled for the orchestrator), along with any MAF graph-level checkpoints recorded for
        /// it (see <see cref="IJsonCheckpointStoreMaintenance"/>). Only permitted once the session
        /// has reached a terminal state (<c>completed</c>/<c>failed</c>/<c>rejected</c>) - deleting
        /// a checkpoint for a still-running or pending-approval session would break resume for a
        /// session that could still legitimately need it. Returns <see langword="false"/> if
        /// checkpointing is disabled or no checkpoint document exists for the session.
        /// </summary>
        public async Task<bool> DeleteCheckpointAsync(
            OrchestratorDefinition orchestrator, string sessionId, CancellationToken cancellationToken = default)
        {
            var config = ResolveCheckpointing(orchestrator);
            if (config is not { Enabled: true })
                return false;

            var document = await _checkpointStore.LoadAsync(sessionId, cancellationToken);
            if (document is null)
                return false;

            if (document.Status is StatusPendingApproval or StatusRunning)
                throw new InvalidOperationException(
                    $"Cannot delete the checkpoint for session '{sessionId}': it is still {document.Status} (only completed, failed, or rejected sessions' checkpoints can be deleted).");

            await _checkpointStore.DeleteAsync(sessionId, cancellationToken);

            // Also clean up MAF's own graph-level checkpoints for this session, if the injected
            // store supports it (see IJsonCheckpointStoreMaintenance) - a raw MAF store that
            // doesn't implement this is left untouched rather than treated as an error.
            if (_mafCheckpointStore is IJsonCheckpointStoreMaintenance maintenance)
                await maintenance.DeleteSessionCheckpointsAsync(sessionId, cancellationToken);

            return true;
        }
    }
}
