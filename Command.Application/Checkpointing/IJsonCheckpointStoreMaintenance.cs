namespace OpenAgentOrchestrator.Command.Application.Checkpointing
{
    /// <summary>
    /// Optional maintenance capability for a <see cref="Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore"/>
    /// implementation: deletes all of a session's Microsoft Agent Framework graph-level
    /// checkpoints. Deliberately separate from MAF's own <c>JsonCheckpointStore</c> contract
    /// (create/retrieve/retrieve-index only) - MAF has no concept of deleting a session's
    /// checkpoints, since it's not something the framework itself ever needs to do, but this
    /// service does (see <see cref="WorkflowCheckpointStore"/> and
    /// <c>WorkflowEngine.DeleteCheckpointAsync</c>). Implementations that don't support cleanup
    /// (e.g. a raw MAF <c>FileSystemJsonCheckpointStore</c>) simply don't implement this
    /// interface; callers should treat it as optional (pattern-match/no-op if absent) rather than
    /// requiring it.
    /// </summary>
    public interface IJsonCheckpointStoreMaintenance
    {
        /// <summary>
        /// Deletes every checkpoint row recorded for <paramref name="sessionId"/>, across the
        /// whole parent-linked chain. Idempotent: deleting a session with no checkpoints is a
        /// no-op, not an error.
        /// </summary>
        Task DeleteSessionCheckpointsAsync(string sessionId, CancellationToken ct = default);
    }
}
