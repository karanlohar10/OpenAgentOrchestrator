using System.Collections.Concurrent;

namespace OpenAgentOrchestrator.Command.Application.Sessions
{
    public interface ISessionStore
    {
        OrchestratorSession Create(string orchestratorId);
        OrchestratorSession? Get(string sessionId);

        /// <summary>
        /// Returns the existing in-memory session for <paramref name="sessionId"/>, or creates and
        /// registers a new one with that exact id (rather than a freshly-generated one) when
        /// missing. Used to rehydrate a session for resume when the in-memory store has lost it -
        /// e.g. the process restarted since the run started - while its durable checkpoint
        /// document still exists.
        /// </summary>
        OrchestratorSession GetOrCreate(string sessionId, string orchestratorId);
    }

    public sealed class InMemorySessionStore : ISessionStore
    {
        private readonly ConcurrentDictionary<string, OrchestratorSession> _sessions = new();

        public OrchestratorSession Create(string orchestratorId)
        {
            var session = new OrchestratorSession { OrchestratorId = orchestratorId };
            _sessions[session.SessionId] = session;
            return session;
        }

        public OrchestratorSession? Get(string sessionId)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return session;
        }

        public OrchestratorSession GetOrCreate(string sessionId, string orchestratorId) =>
            _sessions.GetOrAdd(sessionId, id => new OrchestratorSession { SessionId = id, OrchestratorId = orchestratorId });
    }
}
