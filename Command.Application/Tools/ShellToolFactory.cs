using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Tools
{
    /// <summary>
    /// A shell tool built for a single agent instance: the AI-callable tool itself, the
    /// <see cref="AIContextProvider"/> that surfaces shell/environment awareness to the model, and
    /// the underlying executor (owns the actual OS process(es) and must be disposed with the
    /// agent).
    /// </summary>
    public sealed record ShellToolBinding(AITool Tool, AIContextProvider ContextProvider, IAsyncDisposable Executor);

    public interface IShellToolFactory
    {
        /// <summary>
        /// Builds a local shell tool + environment-awareness context provider from
        /// <paramref name="definition"/> (a "shell"-typed <see cref="ToolDefinition"/>). Throws if
        /// <see cref="ToolDefinition.AcknowledgeUnsafe"/> is not explicitly <see langword="true"/>
        /// - shell execution is inherently unsafe (file system, process, and credential access)
        /// and must be opted into deliberately.
        /// </summary>
        ShellToolBinding Create(ToolDefinition definition);
    }

    /// <summary>
    /// Builds <see cref="Microsoft.Agents.AI.Tools.Shell.LocalShellExecutor"/>-backed shell tools.
    /// See https://learn.microsoft.com/agent-framework/integrations/by-component/tools/shell-tools.
    /// </summary>
    public sealed class ShellToolFactory : IShellToolFactory
    {
        public ShellToolBinding Create(ToolDefinition definition)
        {
            if (!definition.AcknowledgeUnsafe)
            {
                throw new InvalidOperationException(
                    "Shell tool is enabled but 'acknowledgeUnsafe: true' was not set - shell execution can " +
                    "modify files, launch processes, access credentials, and reach external systems; this " +
                    "must be explicitly acknowledged in config.yaml.");
            }

            var mode = string.Equals(definition.Mode, "persistent", StringComparison.OrdinalIgnoreCase)
                ? ShellMode.Persistent
                : ShellMode.Stateless;

            var executor = new LocalShellExecutor(new LocalShellExecutorOptions
            {
                Mode = mode,
                AcknowledgeUnsafe = true
            });

            var contextProvider = new ShellEnvironmentProvider(executor);
            var tool = executor.AsAIFunction(requireApproval: definition.RequireApproval);

            return new ShellToolBinding(tool, contextProvider, executor);
        }
    }
}
