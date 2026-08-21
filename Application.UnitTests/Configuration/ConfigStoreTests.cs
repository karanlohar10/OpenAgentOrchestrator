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

        [TestMethod]
        public void GetConfig_WhenAgentHasInstructionsFile_ResolvesInstructionsFromFile()
        {
            // Arrange
            var instructionsDir = Path.Combine(_tempDir, "instructions");
            Directory.CreateDirectory(instructionsDir);
            File.WriteAllText(Path.Combine(instructionsDir, "planner.md"), "Plan carefully from file.");

            var yaml = ValidYaml.Replace(
                "instructions: \"Plan.\"",
                "instructionsFile: \"planner.md\"");
            File.WriteAllText(_configPath, yaml);

            var sut = CreateSut(instructionsRoot: instructionsDir);

            // Act
            var agent = sut.GetOrchestrator("orch")!.Agents.Single(a => a.Name == "planner");

            // Assert
            agent.Instructions.Should().Be("Plan carefully from file.");
        }

        [TestMethod]
        public void GetConfig_WhenResponseFormatHasSchemaFile_ResolvesSchemaFromFile()
        {
            // Arrange
            var instructionsDir = Path.Combine(_tempDir, "instructions");
            Directory.CreateDirectory(instructionsDir);
            File.WriteAllText(Path.Combine(instructionsDir, "schema.json"), """{"type":"object"}""");

            var yaml = ValidYaml.TrimEnd() + "\n" +
                "        responseFormat:\n" +
                "          type: json_schema\n" +
                "          schemaFile: \"schema.json\"\n";
            File.WriteAllText(_configPath, yaml);

            var sut = CreateSut(instructionsRoot: instructionsDir);

            // Act
            var agent = sut.GetOrchestrator("orch")!.Agents.Single(a => a.Name == "planner");

            // Assert
            agent.ResponseFormat.Should().NotBeNull();
            agent.ResponseFormat!.Schema.Should().Be("""{"type":"object"}""");
        }

        [TestMethod]
        public void GetConfig_WhenBothInlineInstructionsAndInstructionsFileAreSet_InlineWins()
        {
            // Arrange
            var instructionsDir = Path.Combine(_tempDir, "instructions");
            Directory.CreateDirectory(instructionsDir);
            File.WriteAllText(Path.Combine(instructionsDir, "planner.md"), "From file - should be ignored.");

            var yaml = ValidYaml.Replace(
                "instructions: \"Plan.\"",
                "instructions: \"Plan.\"\n        instructionsFile: \"planner.md\"");
            File.WriteAllText(_configPath, yaml);

            var sut = CreateSut(instructionsRoot: instructionsDir);

            // Act
            var agent = sut.GetOrchestrator("orch")!.Agents.Single(a => a.Name == "planner");

            // Assert
            agent.Instructions.Should().Be("Plan.");
        }

        [TestMethod]
        public void Constructor_WhenInstructionsFileIsMissing_Throws()
        {
            // Arrange
            var instructionsDir = Path.Combine(_tempDir, "instructions");
            Directory.CreateDirectory(instructionsDir);

            var yaml = ValidYaml.Replace(
                "instructions: \"Plan.\"",
                "instructionsFile: \"does-not-exist.md\"");
            File.WriteAllText(_configPath, yaml);

            // Act
            var act = () => CreateSut(instructionsRoot: instructionsDir);

            // Assert
            act.Should().Throw<FileNotFoundException>();
        }

        private ConfigStore CreateSut(string? configPath = null, string? instructionsRoot = null)
        {
            var options = Options.Create(new ConfigYamlOptions
            {
                Path = configPath ?? _configPath,
                InstructionsRoot = instructionsRoot ?? Path.Combine(_tempDir, "instructions")
            });
            var validator = new ConfigValidator();
            return new ConfigStore(options, validator, NullLogger<ConfigStore>.Instance);
        }
    }
}
