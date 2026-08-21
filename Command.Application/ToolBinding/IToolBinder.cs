using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    public interface IToolBinder
    {
        string SupportedType { get; }
        Task<IList<AITool>> BindAsync(ToolDefinition definition, CancellationToken cancellationToken = default);
    }
}
