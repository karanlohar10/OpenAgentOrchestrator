using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// Decouples a workflow run's actual execution/draining from the HTTP request that kicked it
    /// off, by running <see cref="IWorkflowEngine.ExecuteAsync"/>/<see cref="IWorkflowEngine.ResumeAsync"/>
    /// in a freshly created DI scope (since <see cref="IWorkflowEngine"/> and its dependencies -
    /// <c>IConfigStore</c>, <c>IAgentFactory</c> - are all request-scoped) and against
    /// <see cref="IHostApplicationLifetime.ApplicationStopping"/> instead of the caller's own
    /// <see cref="CancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// Why this exists: <c>StreamingRun.WatchStreamAsync(CancellationToken)</c> (Microsoft Agent
    /// Framework) is documented to end its event stream silently on cancellation <i>without</i>
    /// cancelling the underlying workflow execution. <see cref="WorkflowEngine"/>'s drain loop has
    /// no way to distinguish "the caller stopped watching" from "the workflow genuinely finished" -
    /// so if the HTTP request's own token were passed straight through, a client disconnecting
    /// mid-run (browser closed, network drop) would make the engine wrongly persist the session as
    /// <c>completed</c>, and would also stop recording any further steps the workflow goes on to
    /// actually complete afterward.
    /// <para/>
    /// Routing every execute/resume call through this coordinator instead means a disconnected
    /// client never interrupts the run: it keeps draining and checkpointing to its true outcome
    /// (<c>completed</c>/<c>pending_approval</c>/<c>failed</c>) in its own scope, and the caller
    /// either receives that real result (if still connected) or the response is simply never
    /// delivered (already-gone client) - session state itself stays correct and queryable/resumable
    /// either way. The <see cref="IHostApplicationLifetime.ApplicationStopping"/> token is used
    /// purely so an in-flight run doesn't hang forever during a genuine process shutdown.
    /// </remarks>
    public sealed class WorkflowExecutionCoordinator : IWorkflowExecutionCoordinator
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHostApplicationLifetime _appLifetime;

        public WorkflowExecutionCoordinator(IServiceScopeFactory scopeFactory, IHostApplicationLifetime appLifetime)
        {
            _scopeFactory = scopeFactory;
            _appLifetime = appLifetime;
        }

        public async Task<ExecuteResponse> ExecuteAsync(OrchestratorDefinition orchestrator, ExecuteRequest request)
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            return await engine.ExecuteAsync(orchestrator, request, _appLifetime.ApplicationStopping);
        }

        public async Task<ExecuteResponse> ResumeAsync(OrchestratorDefinition orchestrator, string sessionId, ResumeRequest request)
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            return await engine.ResumeAsync(orchestrator, sessionId, request, _appLifetime.ApplicationStopping);
        }
    }
}
