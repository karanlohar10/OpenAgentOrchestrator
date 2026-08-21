using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Agents
{
    /// <summary>Creates IChatClient instances for different LLM providers.</summary>
    public interface IChatClientFactory
    {
        IChatClient Create(ProviderDefinition provider, string model);
    }
}
