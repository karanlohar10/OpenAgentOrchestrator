using Microsoft.Agents.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    public interface IAgentFactory
    {
        /// <param name="requireClarificationEnvelope">
        /// When <see langword="true"/> (set by <c>WorkflowEngine</c> when the agent's orchestrator
        /// has <c>checkpointing.humanInLoop.enableClarificationFlag: true</c>), appends instructions
        /// requiring the agent to respond with the clarification JSON envelope - see
        /// <see cref="HumanInLoopDefinition.EnableClarificationFlag"/>.
        /// </param>
        Task<AIAgent> CreateAgentAsync(AgentDefinition agentDef, bool requireClarificationEnvelope = false, CancellationToken cancellationToken = default);
    }
}
