using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    public interface IToolBinderFactory
    {
        Task<ToolBindingResult> BindToolsAsync(IEnumerable<ToolDefinition> toolDefinitions, CancellationToken cancellationToken = default);
    }

    public sealed class ToolBinderFactory : IToolBinderFactory
    {
        private readonly Dictionary<string, IToolBinder> _binders;

        public ToolBinderFactory(IEnumerable<IToolBinder> binders)
        {
            _binders = binders.ToDictionary(b => b.SupportedType, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ToolBindingResult> BindToolsAsync(IEnumerable<ToolDefinition> toolDefinitions, CancellationToken cancellationToken = default)
        {
            var allTools = new List<AITool>();
            var allContextProviders = new List<AIContextProvider>();

            foreach (var toolDef in toolDefinitions)
            {
                if (!_binders.TryGetValue(toolDef.Type, out var binder))
                    throw new InvalidOperationException($"No tool binder registered for type '{toolDef.Type}'.");

                var bound = await binder.BindAsync(toolDef, cancellationToken);
                allTools.AddRange(bound.Tools);
                if (bound.ContextProviders is { Count: > 0 })
                    allContextProviders.AddRange(bound.ContextProviders);
            }

            return new ToolBindingResult(allTools, allContextProviders.Count > 0 ? allContextProviders : null);
        }
    }
}
