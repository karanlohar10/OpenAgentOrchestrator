using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Configuration
{
    [TestClass]
    public sealed class ConfigStoreTests
    {
        private string _tempDir = null!;
        private string _configPath = null!;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "oao-config-store-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "config.yaml");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private const string ValidYaml = """
            providers:
              - id: Azure
                type: azure-openai
                endpoint: "https://example.test"
                apiKey: "key"
            orchestrators:
              - id: orch
                name: Orchestrator
                pattern: sequential
                checkpointing:
                  enabled: false
                agents:
                  - name: planner
                    instructions: "Plan."
                    provider: Azure
                    model: gpt-4o-mini
            """;

        [TestMethod]
        public void GetConfigAndLookups_ReturnParsedObjects()
        {
            // Arrange
            File.WriteAllText(_configPath, ValidYaml);
            var sut = CreateSut();

            // Act
            var storedConfig = sut.GetConfig();
            var storedOrchestrator = sut.GetOrchestrator("orch");
            var storedProvider = sut.GetProvider("azure");

            // Assert
            storedConfig.Providers.Should().ContainSingle(p => p.Id == "Azure" && p.ApiKey == "key");
            storedConfig.Orchestrators.Should().ContainSingle(o => o.Id == "orch");
            storedOrchestrator.Should().NotBeNull();
            storedOrchestrator!.Agents.Should().ContainSingle(a => a.Name == "planner");
            storedProvider.Should().NotBeNull();
            storedProvider!.Id.Should().Be("Azure");
        }

        [TestMethod]
        public void Constructor_WhenConfigFileMissing_Throws()
        {
            // Arrange
            var missingPath = Path.Combine(_tempDir, "does-not-exist.yaml");

            // Act
            var act = () => CreateSut(missingPath);

            // Assert
            act.Should().Throw<FileNotFoundException>();
        }

        [TestMethod]
        public async Task ReloadAsync_WhenNewConfigIsValid_ReplacesInMemorySnapshot()
        {
            // Arrange
            File.WriteAllText(_configPath, ValidYaml);
            var sut = CreateSut();
            sut.GetOrchestrator("orch").Should().NotBeNull();

            File.WriteAllText(_configPath, ValidYaml.Replace("id: orch", "id: orch2").Replace("name: Orchestrator", "name: Orchestrator2"));

            // Act
            var result = await sut.ReloadAsync();

            // Assert
            result.IsValid.Should().BeTrue();
            sut.GetOrchestrator("orch").Should().BeNull();
            sut.GetOrchestrator("orch2").Should().NotBeNull();
        }

        [TestMethod]
        public async Task ReloadAsync_WhenNewConfigIsInvalid_KeepsPreviousSnapshotAndReturnsErrors()
        {
            // Arrange
            File.WriteAllText(_configPath, ValidYaml);
            var sut = CreateSut();

            File.WriteAllText(_configPath, "orchestrators: []");

            // Act
            var result = await sut.ReloadAsync();

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().NotBeEmpty();
            sut.GetOrchestrator("orch").Should().NotBeNull();
        }

        private ConfigStore CreateSut(string? configPath = null)
        {
            var options = Options.Create(new ConfigYamlOptions { Path = configPath ?? _configPath });
            var validator = new ConfigValidator();
            return new ConfigStore(options, validator, NullLogger<ConfigStore>.Instance);
        }
    }
}
