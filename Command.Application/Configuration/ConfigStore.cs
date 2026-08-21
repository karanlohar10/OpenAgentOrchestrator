using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    /// <summary>
    /// Read-only accessor for the platform configuration, loaded from <c>config.yaml</c> on disk
    /// (see <see cref="ConfigYamlOptions"/> for the path) and held in memory. Unlike the original
    /// DB-backed design, providers/orchestrators/agents/tools (including their literal secret
    /// values - API keys, header values, client secrets) all live directly in that one file.
    /// Call <see cref="ReloadAsync"/> (wired to the <c>POST /command/api/v1/config/$reload</c>
    /// endpoint) to pick up edits to <c>config.yaml</c> without restarting the process.
    /// </summary>
    public interface IConfigStore
    {
        Task<PlatformConfig> GetConfigAsync(CancellationToken ct = default);
        Task<OrchestratorDefinition?> GetOrchestratorAsync(string id, CancellationToken ct = default);
        Task<ProviderDefinition?> GetProviderAsync(string id, CancellationToken ct = default);

        /// <summary>Synchronous accessor — reads the in-memory snapshot directly, never blocks on I/O.</summary>
        PlatformConfig GetConfig();
        /// <summary>Synchronous accessor — reads the in-memory snapshot directly, never blocks on I/O.</summary>
        OrchestratorDefinition? GetOrchestrator(string id);
        /// <summary>Synchronous accessor — reads the in-memory snapshot directly, never blocks on I/O.</summary>
        ProviderDefinition? GetProvider(string id);

        /// <summary>
        /// Re-reads and re-validates <c>config.yaml</c> from disk. On validation failure, the
        /// previously loaded (last-known-good) config is kept in memory and the failure is
        /// returned to the caller instead of throwing.
        /// </summary>
        Task<ValidationResult> ReloadAsync(CancellationToken ct = default);

        /// <summary>
        /// Validates <paramref name="candidate"/> against the currently-loaded config: first
        /// resolves any blank/redacted-placeholder secret fields against the real values already
        /// in memory (see <see cref="IConfigMerge"/>), then runs the full validation pipeline.
        /// Does not write to disk or change the in-memory snapshot - used for a "Validate" action
        /// that doesn't require saving first.
        /// </summary>
        Task<ValidationResult> ValidateAsync(PlatformConfig candidate, CancellationToken ct = default);

        /// <summary>
        /// Merges secrets, validates, and - only if valid - serializes <paramref name="candidate"/>
        /// back to <c>config.yaml</c> on disk and swaps it in as the new in-memory snapshot. On
        /// validation failure, nothing is written and the previously loaded config remains active.
        /// </summary>
        Task<ValidationResult> SaveAsync(PlatformConfig candidate, CancellationToken ct = default);
    }

    public sealed class ConfigStore : IConfigStore
    {
        private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private static readonly ISerializer YamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        private readonly string _configYamlPath;
        private readonly string _instructionsRoot;
        private readonly IConfigValidator _configValidator;
        private readonly IConfigMerge _configMerge;
        private readonly ILogger<ConfigStore> _logger;
        private readonly object _lock = new();
        private PlatformConfig _current;

        public ConfigStore(
            IOptions<ConfigYamlOptions> options,
            IConfigValidator configValidator,
            IConfigMerge configMerge,
            ILogger<ConfigStore> logger)
        {
            _configYamlPath = options.Value.Path;
            _instructionsRoot = options.Value.InstructionsRoot;
            _configValidator = configValidator;
            _configMerge = configMerge;
            _logger = logger;
            _current = LoadFromDisk();
        }

        public Task<PlatformConfig> GetConfigAsync(CancellationToken ct = default) => Task.FromResult(GetConfig());

        public Task<OrchestratorDefinition?> GetOrchestratorAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(GetOrchestrator(id));

        public Task<ProviderDefinition?> GetProviderAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(GetProvider(id));

        public PlatformConfig GetConfig()
        {
            lock (_lock)
                return _current;
        }

        public OrchestratorDefinition? GetOrchestrator(string id) =>
            GetConfig().Orchestrators.FirstOrDefault(o => o.Id == id);

        public ProviderDefinition? GetProvider(string id) =>
            GetConfig().Providers?.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public Task<ValidationResult> ReloadAsync(CancellationToken ct = default)
        {
            PlatformConfig reloaded;
            try
            {
                reloaded = LoadFromDisk();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload config.yaml from '{Path}'", _configYamlPath);
                return Task.FromResult(new ValidationResult
                {
                    IsValid = false,
                    Errors = [$"Failed to read/parse config.yaml: {ex.Message}"]
                });
            }

            var validation = _configValidator.Validate(reloaded);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Rejected config.yaml reload from '{Path}': {Errors}", _configYamlPath, string.Join("; ", validation.Errors));
                return Task.FromResult(validation);
            }

            lock (_lock)
                _current = reloaded;

            _logger.LogInformation("Reloaded config.yaml from '{Path}'", _configYamlPath);
            return Task.FromResult(validation);
        }

        public Task<ValidationResult> ValidateAsync(PlatformConfig candidate, CancellationToken ct = default)
        {
            var (validation, _) = MergeAndValidate(candidate);
            return Task.FromResult(validation);
        }

        public Task<ValidationResult> SaveAsync(PlatformConfig candidate, CancellationToken ct = default)
        {
            var (validation, merged) = MergeAndValidate(candidate);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Rejected config save: {Errors}", string.Join("; ", validation.Errors));
                return Task.FromResult(validation);
            }

            var yaml = YamlSerializer.Serialize(merged);
            File.WriteAllText(_configYamlPath, yaml);

            lock (_lock)
                _current = merged;

            _logger.LogInformation("Saved config.yaml to '{Path}'", _configYamlPath);
            return Task.FromResult(validation);
        }

        /// <summary>
        /// Runs the merge (secret-sentinel resolution against the current in-memory config) +
        /// validate pipeline shared by <see cref="ValidateAsync"/> and <see cref="SaveAsync"/>.
        /// Merge errors (unresolved secrets on new entities) are folded into the returned
        /// <see cref="ValidationResult"/> alongside the normal validator's errors.
        /// </summary>
        private (ValidationResult Validation, PlatformConfig Candidate) MergeAndValidate(PlatformConfig candidate)
        {
            candidate.Orchestrators ??= [];
            var previous = GetConfig();

            var mergeErrors = _configMerge.MergeSecrets(previous, candidate);

            var validation = _configValidator.Validate(candidate);
            validation.Errors.InsertRange(0, mergeErrors);
            validation.IsValid = validation.Errors.Count == 0;

            return (validation, candidate);
        }

        private PlatformConfig LoadFromDisk()
        {
            if (!File.Exists(_configYamlPath))
            {
                throw new FileNotFoundException(
                    $"config.yaml not found at '{_configYamlPath}'. Copy config.sample.yaml to config.yaml " +
                    "and fill in real provider/tool secrets (config.yaml is gitignored).", _configYamlPath);
            }

            var yaml = File.ReadAllText(_configYamlPath);
            var config = YamlDeserializer.Deserialize<PlatformConfig>(yaml)
                ?? throw new InvalidOperationException($"config.yaml at '{_configYamlPath}' deserialized to null.");

            config.Orchestrators ??= [];
            ResolveInstructionsAndSchemaFiles(config);
            return config;
        }

        /// <summary>
        /// Resolves <see cref="AgentDefinition.InstructionsFile"/> and
        /// <see cref="ResponseFormatDefinition.SchemaFile"/> references (paths relative to the
        /// configured <see cref="ConfigYamlOptions.InstructionsRoot"/>) into the in-memory
        /// <see cref="AgentDefinition.Instructions"/> / <see cref="ResponseFormatDefinition.Schema"/>
        /// values, letting long/complex prompts and JSON schemas live as standalone files instead
        /// of inline YAML strings. An inline value, if also present, always wins (the file
        /// reference is simply not read in that case, and a warning is logged).
        /// </summary>
        private void ResolveInstructionsAndSchemaFiles(PlatformConfig config)
        {
            foreach (var orchestrator in config.Orchestrators)
            {
                foreach (var agent in orchestrator.Agents)
                {
                    if (!string.IsNullOrWhiteSpace(agent.InstructionsFile))
                    {
                        if (!string.IsNullOrWhiteSpace(agent.Instructions))
                        {
                            _logger.LogWarning(
                                "Orchestrator '{OrchestratorId}', agent '{AgentName}': both inline 'instructions' and 'instructionsFile' " +
                                "are set - the inline value wins and 'instructionsFile' ({InstructionsFile}) is ignored.",
                                orchestrator.Id, agent.Name, agent.InstructionsFile);
                        }
                        else
                        {
                            agent.Instructions = ReadInstructionsFile(orchestrator.Id, agent.Name, "instructionsFile", agent.InstructionsFile);
                        }
                    }

                    if (agent.ResponseFormat is { } responseFormat && !string.IsNullOrWhiteSpace(responseFormat.SchemaFile))
                    {
                        if (!string.IsNullOrWhiteSpace(responseFormat.Schema))
                        {
                            _logger.LogWarning(
                                "Orchestrator '{OrchestratorId}', agent '{AgentName}': both inline 'responseFormat.schema' and " +
                                "'responseFormat.schemaFile' are set - the inline value wins and 'schemaFile' ({SchemaFile}) is ignored.",
                                orchestrator.Id, agent.Name, responseFormat.SchemaFile);
                        }
                        else
                        {
                            responseFormat.Schema = ReadInstructionsFile(orchestrator.Id, agent.Name, "responseFormat.schemaFile", responseFormat.SchemaFile);
                        }
                    }
                }
            }
        }

        private string ReadInstructionsFile(string orchestratorId, string agentName, string fieldLabel, string relativePath)
        {
            var fullPath = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(_instructionsRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"Orchestrator '{orchestratorId}', agent '{agentName}': {fieldLabel} '{relativePath}' " +
                    $"was not found under instructions root '{_instructionsRoot}' (resolved to '{fullPath}').",
                    fullPath);
            }

            return File.ReadAllText(fullPath);
        }
    }

    /// <summary>Binds the <c>ConfigYaml</c> appsettings.json section.</summary>
    public sealed class ConfigYamlOptions
    {
        /// <summary>
        /// Path to config.yaml, relative to the content root unless absolute. Defaults to
        /// "config.yaml".
        /// </summary>
        public string Path { get; set; } = "config.yaml";

        /// <summary>
        /// Root directory that <see cref="AgentDefinition.InstructionsFile"/> and
        /// <see cref="ResponseFormatDefinition.SchemaFile"/> paths are resolved relative to
        /// (relative to the content root unless absolute). Defaults to "instructions". Lets
        /// agent prompts and JSON response schemas be authored as standalone files instead of
        /// inline YAML strings - see <c>Service/config.sample.yaml</c> for examples.
        /// </summary>
        public string InstructionsRoot { get; set; } = "instructions";
    }
}
