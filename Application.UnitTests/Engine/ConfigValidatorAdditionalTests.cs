using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    [TestClass]
    public sealed class ConfigValidatorAdditionalTests
    {
        [TestMethod]
        public void Validate_DuplicateProviderIds_ReturnsError()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Providers =
            [
                new ProviderDefinition { Id = "azure", Type = "azure-openai", Endpoint = "https://example.test", ApiKey = "key" },
                new ProviderDefinition { Id = "azure", Type = "azure-openai", Endpoint = "https://example2.test", ApiKey = "key" }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.Contains("Duplicate provider id"));
        }

        [TestMethod]
        public void Validate_AzureOpenAiProviderWithoutEndpoint_ReturnsError()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Providers =
            [
                new ProviderDefinition { Id = "azure", Type = "azure-openai", ApiKey = "key" }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.Contains("requires an endpoint"));
        }

        [TestMethod]
        public void Validate_NoOrchestrators_ReturnsError()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Orchestrators = [];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain("At least one orchestrator must be defined.");
        }

        [TestMethod]
        public void Validate_ProviderWithoutApiKey_ReturnsWarning()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Providers =
            [
                new ProviderDefinition { Id = "azure", Type = "azure-openai", Endpoint = "https://example.test" }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeTrue();
            result.Warnings.Should().Contain(warning => warning.Contains("no apiKey configured"));
        }

        [TestMethod]
        public void Validate_ProviderWithApiKey_ReturnsNoWarning()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Providers =
            [
                new ProviderDefinition { Id = "azure", Type = "azure-openai", Endpoint = "https://example.test", ApiKey = "real-key" }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.Warnings.Should().BeEmpty();
        }

        [TestMethod]
        public void Validate_InvalidProviderType_ReturnsError()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Providers =
            [
                new ProviderDefinition { Id = "ollama-local", Type = "ollama" }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.Contains("invalid type 'ollama'"));
        }

        [TestMethod]
        public void Validate_ToolHeaderIsLiteral_ReturnsNoWarning()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition
                {
                    Type = "mcp",
                    Name = "search_templates",
                    Endpoint = "https://example.test/mcp",
                    Headers = new Dictionary<string, string> { ["X-API-Key"] = "literal-value" }
                }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.Warnings.Should().BeEmpty();
        }

        [TestMethod]
        public void Validate_BearerToolMissingAllRequiredFields_ReturnsAllErrors()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition
                {
                    Type = "mcp",
                    Name = "secure_tool",
                    Endpoint = "https://example.test/mcp",
                    AuthType = "bearer"
                }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(error => error.Contains("bearer auth requires a tokenEndpoint"));
            result.Errors.Should().Contain(error => error.Contains("bearer auth requires a clientId"));
            result.Errors.Should().Contain(error => error.Contains("bearer auth requires a clientSecret"));
        }

        [TestMethod]
        public void Validate_BearerToolWithAllRequiredFields_ReturnsValid()
        {
            // Arrange
            var config = CreateBaseConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition
                {
                    Type = "mcp",
                    Name = "secure_tool",
                    Endpoint = "https://example.test/mcp",
                    AuthType = "bearer",
                    TokenEndpoint = "https://example.test/token",
                    ClientId = "client-id",
                    ClientSecret = "real-client-secret"
                }
            ];

            // Act
            var result = new ConfigValidator().Validate(config);

            // Assert
            result.IsValid.Should().BeTrue();
        }

        private static PlatformConfig CreateBaseConfig() =>
            new()
            {
                Orchestrators =
                [
                    new OrchestratorDefinition
                    {
                        Id = "orch",
                        Name = "Orchestrator",
                        Pattern = "sequential",
                        Checkpointing = new CheckpointingDefinition(),
                        Agents = [new AgentDefinition { Name = "planner", Instructions = "Plan.", Provider = "azure", Model = "gpt-4o" }]
                    }
                ]
            };
    }
}
