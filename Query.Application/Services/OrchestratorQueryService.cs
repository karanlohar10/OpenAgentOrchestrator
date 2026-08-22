using OpenAgentOrchestrator.Command.Application.Checkpointing;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.Sessions;
using OpenAgentOrchestrator.Query.Domain.Model;

namespace OpenAgentOrchestrator.Query.Application.Services
{
    public interface IOrchestratorQueryService
    {
        /// <summary>Returns the orchestrator's summary, or null if it isn't defined in config.yaml.</summary>
        OrchestratorSummaryResponse? GetOrchestratorSummary(string orchestratorId);

        /// <summary>Returns a session's current status, or null if the session doesn't exist.</summary>
        SessionStatusResponse? GetSessionStatus(string sessionId);

        /// <summary>
        /// Returns the durable checkpoint document for a session, or null if no checkpoint
        /// exists for it, its orchestrator is no longer configured, or checkpointing is disabled.
        /// </summary>
        Task<SessionCheckpointsResponse?> GetCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Thin read-side service wrapping the shared (Command.Application-owned) config store,
    /// session store, and workflow engine's checkpoint accessor - there is no separate read
    /// database; the query side simply projects the same in-memory/on-disk state into
    /// <c>Query.Domain.Model</c> response DTOs.
    /// </summary>
    public sealed class OrchestratorQueryService : IOrchestratorQueryService
    {
        private readonly IConfigStore _configStore;
        private readonly ISessionStore _sessionStore;
        private readonly IWorkflowEngine _workflowEngine;
        private readonly IWorkflowCheckpointStore _checkpointStore;

        public OrchestratorQueryService(
            IConfigStore configStore, ISessionStore sessionStore, IWorkflowEngine workflowEngine, IWorkflowCheckpointStore checkpointStore)
        {
            _configStore = configStore;
            _sessionStore = sessionStore;
            _workflowEngine = workflowEngine;
            _checkpointStore = checkpointStore;
        }

        public OrchestratorSummaryResponse? GetOrchestratorSummary(string orchestratorId)
        {
            var orchestrator = _configStore.GetOrchestrator(orchestratorId);
            if (orchestrator is null)
                return null;

            return new OrchestratorSummaryResponse
            {
                Id = orchestrator.Id,
                Name = orchestrator.Name,
                Description = orchestrator.Description,
                Pattern = orchestrator.Pattern,
                AgentCount = orchestrator.Agents.Count
            };
        }

        public SessionStatusResponse? GetSessionStatus(string sessionId)
        {
            var session = _sessionStore.Get(sessionId);
            if (session is null)
                return null;

            return new SessionStatusResponse
            {
                SessionId = session.SessionId,
                OrchestratorId = session.OrchestratorId,
                Status = session.Status,
                CreatedAt = session.CreatedAt,
                CompletedAt = session.CompletedAt,
                PendingApprovalPrompt = session.PendingApprovalPrompt,
                PendingStepIndex = session.PendingStepIndex,
                PendingAgentName = session.PendingAgentName,
                PendingOutput = session.PendingOutput,
                PendingNeedsClarification = session.PendingNeedsClarification,
                PendingClarificationQuestion = session.PendingClarificationQuestion
            };
        }

        public async Task<SessionCheckpointsResponse?> GetCheckpointsAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var checkpoint = await _checkpointStore.LoadAsync(sessionId, cancellationToken);
            if (checkpoint is null)
                return null;

            var orchestrator = await _configStore.GetOrchestratorAsync(checkpoint.OrchestratorId, cancellationToken);
            if (orchestrator is null)
                return null;

            var document = await _workflowEngine.GetCheckpointsAsync(orchestrator, sessionId, cancellationToken);
            if (document is null)
                return null;

            return new SessionCheckpointsResponse
            {
                SessionId = document.SessionId,
                OrchestratorId = document.OrchestratorId,
                Pattern = document.Pattern,
                Input = document.Input,
                CreatedAt = document.CreatedAt,
                UpdatedAt = document.UpdatedAt,
                Status = document.Status,
                FinalOutput = document.FinalOutput,
                Error = document.Error,
                Steps = document.Steps.Select(s => new StepCheckpointRecordResponse
                {
                    StepIndex = s.StepIndex,
                    AgentName = s.AgentName,
                    Status = s.Status,
                    Output = s.Output,
                    DurationMs = s.DurationMs,
                    RecordedAt = s.RecordedAt,
                    Edited = s.Edited
                }).ToList(),
                PendingAgentName = document.PendingAgentName,
                PendingStepIndex = document.PendingStepIndex,
                PendingOutput = document.PendingOutput,
                PendingApprovalPrompt = document.PendingApprovalPrompt,
                PendingNeedsClarification = document.PendingNeedsClarification,
                PendingClarificationQuestion = document.PendingClarificationQuestion
            };
        }
    }
}
