using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    public interface IToolBinderFactory
    {
        Task<IList<AITool>> BindToolsAsync(IEnumerable<ToolDefinition> toolDefinitions, CancellationToken cancellationToken = default);
    }

    public sealed class ToolBinderFactory : IToolBinderFactory
    {
        private readonly Dictionary<string, IToolBinder> _binders;

        public ToolBinderFactory(IEnumerable<IToolBinder> binders)
        {
            _binders = binders.ToDictionary(b => b.SupportedType, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<IList<AITool>> BindToolsAsync(IEnumerable<ToolDefinition> toolDefinitions, CancellationToken cancellationToken = default)
        {
            var allTools = new List<AITool>();

            foreach (var toolDef in toolDefinitions)
            {
                if (!_binders.TryGetValue(toolDef.Type, out var binder))
                    throw new InvalidOperationException($"No tool binder registered for type '{toolDef.Type}'.");

                var tools = await binder.BindAsync(toolDef, cancellationToken);
                allTools.AddRange(tools);
            }

            return allTools;
        }
    }
}
