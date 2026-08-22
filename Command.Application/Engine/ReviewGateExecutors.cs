using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// The payload of a per-step human-in-the-loop request, raised on a per-step RequestPort (see
    /// <see cref="StepReviewExecutor"/>) and surfaced as <c>ExternalRequest.Data</c> on the
    /// resulting <c>RequestInfoEvent</c>. <see cref="NeedsClarification"/>/
    /// <see cref="ClarificationQuestion"/> are only ever non-default when the orchestrator has
    /// <c>checkpointing.humanInLoop.enableClarificationFlag: true</c> and the agent's response
    /// parsed as a <see cref="ClarificationEnvelope"/> - otherwise they are always
    /// <see langword="false"/>/<see langword="null"/>, matching pre-existing behavior exactly.
    /// </summary>
    public sealed record StepReviewRequest(
        string AgentName,
        int StepIndex,
        string Output,
        string? ApprovalPrompt,
        bool NeedsClarification = false,
        string? ClarificationQuestion = null);

    /// <summary>
    /// Sits immediately downstream of one agent in a "reviewable sequential" workflow graph (built
    /// by <see cref="WorkflowEngine.BuildReviewableSequentialWorkflow"/> when an orchestrator has
    /// both <c>humanInLoop.enabled</c> and the sequential pattern). One instance per step both
    /// *sends* the per-step human-in-the-loop request (to this step's dedicated
    /// <c>RequestPort&lt;StepReviewRequest, string&gt;</c>) and *receives* that port's response back
    /// on the very same node - mirroring the request/response idiom used by the Agent Framework's
    /// own request-port samples (a single stateful executor both issues a request to a port and
    /// consumes the port's eventual answer), rather than splitting "send" and "receive" across two
    /// separate one-way executors.
    ///
    /// Distinguishes the agent's *generated* response (an assistant-authored
    /// <see cref="ChatMessage"/> batch) - which is routed to the port to raise a
    /// human-in-the-loop request - from any *forwarded input* batch the agent also relays (its
    /// <c>ForwardIncomingMessages</c> pass-through behavior), which carries no reviewable content
    /// and is discarded so it cannot race ahead of the real reviewed output. The agent's own
    /// <see cref="TurnToken"/> broadcast is likewise discarded here (not relayed), so it cannot race
    /// ahead of the (possibly much later, possibly cross-process) human-in-the-loop response.
    ///
    /// When the orchestrator has <c>enableClarificationFlag: true</c>, the agent's response is
    /// additionally parsed as a <see cref="ClarificationEnvelope"/>; whether the *last* request sent
    /// needed clarification is remembered across the pause via MAF's durable per-executor state
    /// (<see cref="IWorkflowContext.QueueStateUpdateAsync{T}"/>/
    /// <see cref="IWorkflowContext.ReadOrInitStateAsync{T}"/> - checkpoint-persisted, survives
    /// process restarts). On receiving the human's answer: if the last request needed
    /// clarification, the answer is routed back to loop into <b>the same agent</b> for another turn
    /// (so a follow-up question from that same agent is possible); otherwise (or when the flag is
    /// disabled) the answer is forwarded to the next agent - or yielded as the terminal workflow
    /// output for the last step - exactly as before this feature existed. Every step still pauses
    /// unconditionally; this class only changes what happens <i>after</i> the human answers.
    /// </summary>
    internal sealed class StepReviewExecutor(
        string id,
        int stepIndex,
        string agentName,
        string? approvalPrompt,
        bool enableClarificationFlag,
        string selfAgentExecutorId,
        string? nextAgentExecutorId,
        bool isTerminal)
        : Executor(id)
    {
        private const string NeedsClarificationStateKey = "needsClarification";

        public int StepIndex { get; } = stepIndex;
        public string AgentName { get; } = agentName;

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        {
            var builder = protocolBuilder
                .SendsMessage<StepReviewRequest>()
                .SendsMessage<List<ChatMessage>>()
                .SendsMessage<TurnToken>();

            if (isTerminal)
                builder = builder.YieldsOutput<List<ChatMessage>>();

            return builder.ConfigureRoutes(routes => routes
                .AddHandler<List<ChatMessage>>(HandleMessagesAsync)
                .AddHandler<TurnToken>(HandleTurnTokenAsync)
                .AddHandler<string>(HandleAnswerAsync));
        }

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

            var needsClarification = false;
            string? clarificationQuestion = null;
            var output = assistantText;

            if (enableClarificationFlag)
            {
                if (ClarificationEnvelope.TryParse(assistantText, out var envelope))
                {
                    needsClarification = envelope.NeedsClarification;
                    clarificationQuestion = envelope.ClarificationQuestion;
                    output = envelope.Content ?? assistantText;
                }
                // Malformed/missing envelope: fail safe - treat as a routine (non-clarification)
                // response using the raw text. The step still pauses for review as normal; only the
                // clarification metadata is unavailable for this round.
            }

            await context.QueueStateUpdateAsync(NeedsClarificationStateKey, needsClarification, ct);
            await context.SendMessageAsync(
                new StepReviewRequest(AgentName, StepIndex, output, approvalPrompt, needsClarification, clarificationQuestion),
                ct);
        }

        /// <summary>
        /// Swallows the agent's own <see cref="TurnToken"/> broadcast rather than relaying it - see
        /// class remarks for why relaying it here would race ahead of the (possibly much later,
        /// possibly cross-process) human-in-the-loop response.
        /// </summary>
        private static ValueTask HandleTurnTokenAsync(TurnToken token, IWorkflowContext context, CancellationToken ct) =>
            default;

        private async ValueTask HandleAnswerAsync(string reviewedOutput, IWorkflowContext context, CancellationToken ct)
        {
            var needsClarification = await context.ReadOrInitStateAsync(NeedsClarificationStateKey, static () => false, ct);

            // Sent as a User-authored message (not Assistant) - it becomes the target agent's next
            // input, and IChatClient implementations look for the last User message as "the
            // prompt". For the terminal step (no next agent, no loop-back needed),
            // WorkflowEngine.ExtractFinalAssistantText falls back to concatenating all message text
            // when no Assistant message is present, so the final output is unaffected.
            var messages = new List<ChatMessage> { new(ChatRole.User, reviewedOutput) };

            if (needsClarification)
            {
                // The agent itself asked this question, so the human's answer becomes that SAME
                // agent's next input turn (not the next agent's). Targeted send is required here
                // (rather than the untargeted broadcast overload) because this node has more than
                // one possible List<ChatMessage>/TurnToken destination (self loop-back vs. forward).
                await context.SendMessageAsync(messages, selfAgentExecutorId, ct);
                await context.SendMessageAsync(new TurnToken(emitEvents: true), selfAgentExecutorId, ct);
                return;
            }

            if (isTerminal)
            {
                await context.YieldOutputAsync(messages, ct);
                return;
            }

            await context.SendMessageAsync(messages, nextAgentExecutorId!, ct);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), nextAgentExecutorId!, ct);
        }
    }
}
