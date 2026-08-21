using Microsoft.Agents.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    public interface IAgentFactory
    {
        Task<AIAgent> CreateAgentAsync(AgentDefinition agentDef, CancellationToken cancellationToken = default);
    }
}
