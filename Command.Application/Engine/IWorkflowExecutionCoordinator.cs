using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// Runs an orchestrator's <see cref="IWorkflowEngine.ExecuteAsync"/>/<see cref="IWorkflowEngine.ResumeAsync"/>
    /// in a DI scope and cancellation lifetime of its own, decoupled from the calling HTTP
    /// request. This is what prevents a client disconnecting (browser closed, network drop,
    /// client-side abort) mid-run from being mistaken for the workflow having finished - see
    /// <see cref="WorkflowExecutionCoordinator"/> for the full rationale.
    /// </summary>
    public interface IWorkflowExecutionCoordinator
    {
        Task<ExecuteResponse> ExecuteAsync(OrchestratorDefinition orchestrator, ExecuteRequest request);

        Task<ExecuteResponse> ResumeAsync(OrchestratorDefinition orchestrator, string sessionId, ResumeRequest request);
    }
}
