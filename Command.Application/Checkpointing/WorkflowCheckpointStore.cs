using System.Text.Json;

namespace OpenAgentOrchestrator.Command.Application.Checkpointing
{
    /// <summary>
    /// Reads and writes durable, per-session <see cref="SessionCheckpointDocument"/> checkpoint
    /// manifests. The default implementation (<see cref="JsonFileWorkflowCheckpointStore"/>)
    /// persists one JSON file per session to disk. It is deliberately separate from Microsoft
    /// Agent Framework's own graph-level <c>CheckpointManager</c>/<c>FileSystemJsonCheckpointStore</c>
    /// checkpoints, which do not support the step-level manifest this store provides.
    /// </summary>
    public interface IWorkflowCheckpointStore
    {
        /// <summary>
        /// Creates (or overwrites) the checkpoint document for a session. Implementations backed
        /// by an optimistic-concurrency store (e.g. Postgres) should throw
        /// <see cref="WorkflowCheckpointConcurrencyException"/> - rather than silently
        /// overwriting - if the document was modified by another writer since it was loaded.
        /// </summary>
        Task SaveAsync(SessionCheckpointDocument document, CancellationToken ct = default);

        /// <summary>Loads the checkpoint document for a session, or null if none exists.</summary>
        Task<SessionCheckpointDocument?> LoadAsync(string sessionId, CancellationToken ct = default);

        /// <summary>
        /// Deletes the checkpoint document for a session, if one exists. Idempotent: deleting a
        /// session with no checkpoint is a no-op, not an error.
        /// </summary>
        Task DeleteAsync(string sessionId, CancellationToken ct = default);
    }

    /// <summary>
    /// Thrown by an <see cref="IWorkflowCheckpointStore"/> implementation when a
    /// <see cref="IWorkflowCheckpointStore.SaveAsync"/> call detects that the checkpoint document
    /// was modified by another writer since it was loaded (optimistic-concurrency conflict).
    /// </summary>
    public sealed class WorkflowCheckpointConcurrencyException(string sessionId, Exception? innerException = null)
        : Exception($"Checkpoint for session '{sessionId}' was updated concurrently by another writer.", innerException)
    {
        public string SessionId { get; } = sessionId;
    }

    /// <summary>
    /// File-system-backed <see cref="IWorkflowCheckpointStore"/>: one JSON file per session, named
    /// <c>{sessionId}.json</c>, under a configurable root directory passed to the constructor.
    /// </summary>
    public sealed class JsonFileWorkflowCheckpointStore : IWorkflowCheckpointStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly string _rootDirectory;

        public JsonFileWorkflowCheckpointStore(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            _rootDirectory = rootDirectory;
        }

        public async Task SaveAsync(SessionCheckpointDocument document, CancellationToken ct = default)
        {
            Directory.CreateDirectory(_rootDirectory);
            document.UpdatedAt = DateTime.UtcNow;

            var path = GetPath(document.SessionId);
            var json = JsonSerializer.Serialize(document, SerializerOptions);

            // Write to a temp file then move into place, so a crash mid-write never leaves a
            // corrupted/partial checkpoint file behind for a subsequent resume attempt to read.
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, path, overwrite: true);
        }

        public async Task<SessionCheckpointDocument?> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            var path = GetPath(sessionId);
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<SessionCheckpointDocument>(json, SerializerOptions);
        }

        public Task DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            var path = GetPath(sessionId);
            if (File.Exists(path))
                File.Delete(path);

            return Task.CompletedTask;
        }

        private string GetPath(string sessionId) => Path.Combine(_rootDirectory, $"{sessionId}.json");
    }
}
