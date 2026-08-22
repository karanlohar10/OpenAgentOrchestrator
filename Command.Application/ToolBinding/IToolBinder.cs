using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    /// <summary>
    /// The result of binding a single <see cref="ToolDefinition"/>: the AI-callable tool(s) it
    /// produces, and (for tools such as "shell" that also need to surface ambient context to the
    /// model) any <see cref="AIContextProvider"/>s it produces alongside them.
    /// </summary>
    public sealed record ToolBindingResult(IList<AITool> Tools, IList<AIContextProvider>? ContextProviders = null);

    public interface IToolBinder
    {
        string SupportedType { get; }
        Task<ToolBindingResult> BindAsync(ToolDefinition definition, CancellationToken cancellationToken = default);
    }
}
