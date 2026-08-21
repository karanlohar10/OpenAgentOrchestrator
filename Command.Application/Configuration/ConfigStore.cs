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
    }

    public sealed class ConfigStore : IConfigStore
    {
        private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private readonly string _configYamlPath;
        private readonly IConfigValidator _configValidator;
        private readonly ILogger<ConfigStore> _logger;
        private readonly object _lock = new();
        private PlatformConfig _current;

        public ConfigStore(
            IOptions<ConfigYamlOptions> options,
            IConfigValidator configValidator,
            ILogger<ConfigStore> logger)
        {
            _configYamlPath = options.Value.Path;
            _configValidator = configValidator;
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
            return config;
        }
    }

    /// <summary>Binds the <c>ConfigYaml</c> appsettings.json section - just the file path.</summary>
    public sealed class ConfigYamlOptions
    {
        /// <summary>
        /// Path to config.yaml, relative to the content root unless absolute. Defaults to
        /// "config.yaml".
        /// </summary>
        public string Path { get; set; } = "config.yaml";
    }
}
