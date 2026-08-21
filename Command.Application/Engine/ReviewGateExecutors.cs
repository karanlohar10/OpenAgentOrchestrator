using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// The payload of a per-step human-in-the-loop request, raised on a per-step RequestPort (see
    /// <see cref="StepReviewGateExecutor"/>) and surfaced as <c>ExternalRequest.Data</c> on the
    /// resulting <c>RequestInfoEvent</c>.
    /// </summary>
    public sealed record StepReviewRequest(string AgentName, int StepIndex, string Output, string? ApprovalPrompt);

    /// <summary>
    /// Sits immediately downstream of one agent in a "reviewable sequential" workflow graph (built
    /// by <see cref="WorkflowEngine.BuildReviewableSequentialWorkflow"/> when an orchestrator has
    /// both <c>humanInLoop.enabled</c> and the sequential pattern). Distinguishes the agent's
    /// *generated* response (an assistant-authored <see cref="ChatMessage"/> batch) - which is
    /// routed to a per-step RequestPort to raise a human-in-the-loop request - from any
    /// *forwarded input* batch the agent also relays (its <c>ForwardIncomingMessages</c>
    /// pass-through behavior), which carries no reviewable content and is discarded so it cannot
    /// race ahead of the real reviewed output. The agent's own <see cref="TurnToken"/> broadcast is
    /// likewise discarded here (not relayed) - see <see cref="StepReviewCompleteExecutor"/>
    /// remarks for why.
    /// </summary>
    internal sealed class StepReviewGateExecutor(string id, int stepIndex, string agentName, string? approvalPrompt)
        : Executor(id)
    {
        public int StepIndex { get; } = stepIndex;
        public string AgentName { get; } = agentName;

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
            protocolBuilder.SendsMessage<StepReviewRequest>()
                .ConfigureRoutes(routes => routes
                    .AddHandler<List<ChatMessage>>(HandleMessagesAsync)
                    .AddHandler<TurnToken>(HandleTurnTokenAsync));

        private async ValueTask HandleMessagesAsync(List<ChatMessage> messages, IWorkflowContext context, CancellationToken ct)
        {
            var assistantText = WorkflowEngine.ExtractFinalAssistantTextOrNull(messages);
            if (assistantText is null)
            {
                // Just the agent's forwarded input (ForwardIncomingMessages pass-through), not the
                // agent's own generated output - swallow it, otherwise it would race ahead of (and
                // be overwritten by) the real reviewed output once the human-in-the-loop request is
                // answered, causing the next agent to run against stale/wrong input.
                return;
            }

            await context.SendMessageAsync(new StepReviewRequest(AgentName, StepIndex, assistantText, approvalPrompt), ct);
        }

        /// <summary>
        /// Swallows the agent's own <see cref="TurnToken"/> broadcast rather than relaying it - see
        /// <see cref="StepReviewCompleteExecutor"/> remarks for why relaying it here would race
        /// ahead of the (possibly much later, possibly cross-process) human-in-the-loop response.
        /// </summary>
        private static ValueTask HandleTurnTokenAsync(TurnToken token, IWorkflowContext context, CancellationToken ct) =>
            default;
    }

    /// <summary>
    /// Sits downstream of the per-step <c>RequestPort&lt;StepReviewRequest, string&gt;</c>. Wraps
    /// the port's response - the possibly human-edited step output - into a
    /// <see cref="ChatMessage"/> batch and forwards it, together with a freshly-minted
    /// <see cref="TurnToken"/>, to the next agent in the pipeline (or to the workflow's terminal
    /// output, for the last step).
    /// </summary>
    internal sealed class StepReviewCompleteExecutor(string id, bool isTerminal = false) : Executor(id)
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
            protocolBuilder.SendsMessage<List<ChatMessage>>()
                .SendsMessage<TurnToken>()
                .YieldsOutput<List<ChatMessage>>()
                .ConfigureRoutes(routes => routes
                    .AddHandler<string>(HandleResponseAsync));

        private async ValueTask HandleResponseAsync(string reviewedOutput, IWorkflowContext context, CancellationToken ct)
        {
            // Sent as a User-authored message (not Assistant) - it becomes the *next* agent's
            // input, and IChatClient implementations look for the last User message as "the
            // prompt". For the terminal step (no next agent), WorkflowEngine.ExtractFinalAssistantText
            // falls back to concatenating all message text when no Assistant message is present, so
            // the final output is unaffected.
            var messages = new List<ChatMessage> { new(ChatRole.User, reviewedOutput) };

            if (isTerminal)
            {
                await context.YieldOutputAsync(messages, ct);
                return;
            }

            await context.SendMessageAsync(messages, ct);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), ct);
        }
    }
}
