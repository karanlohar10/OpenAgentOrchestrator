using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Application.Tools;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    /// <summary>
    /// Binds the local shell tool ("shell" tool type) - a
    /// <c>Microsoft.Agents.AI.Tools.Shell.LocalShellExecutor</c>-backed <see cref="AITool"/> plus
    /// the <see cref="AIContextProvider"/> that surfaces shell/environment awareness to the model.
    /// Delegates the actual construction to <see cref="IShellToolFactory"/>.
    /// </summary>
    public sealed class ShellToolBinder : IToolBinder
    {
        private readonly IShellToolFactory _shellToolFactory;

        public ShellToolBinder(IShellToolFactory shellToolFactory)
        {
            _shellToolFactory = shellToolFactory;
        }

        public string SupportedType => "shell";

        public Task<ToolBindingResult> BindAsync(ToolDefinition definition, CancellationToken cancellationToken = default)
        {
            // NOTE: the shell executor is created fresh per binding and is not explicitly
            // disposed - acceptable for this hackathon-scale service, but a long-running
            // production deployment should track and dispose it alongside the agent/workflow
            // session lifetime.
            var binding = _shellToolFactory.Create(definition);

            return Task.FromResult(new ToolBindingResult(
                [binding.Tool],
                [binding.ContextProvider]));
        }
    }
}
