using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    [TestClass]
    public class ConfigValidatorTests
    {
        [TestMethod]
        public void Validate_ValidConfig_ReturnsValid()
        {
            var config = CreateValidConfig();
            var validator = new ConfigValidator();

            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(0, result.Errors.Count);
        }

        [TestMethod]
        public void Validate_DuplicateOrchestratorIds_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators.Add(new OrchestratorDefinition
            {
                Id = "test-orch",
                Name = "Duplicate",
                Pattern = "sequential",
                Checkpointing = new CheckpointingDefinition(),
                Agents = [new AgentDefinition { Name = "a", Instructions = "i", Provider = "azure", Model = "gpt-4o" }]
            });

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("Duplicate orchestrator id")));
        }

        [TestMethod]
        public void Validate_InvalidPattern_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Pattern = "concurrent";

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("invalid pattern")));
        }

        [TestMethod]
        public void Validate_McpToolWithoutEndpoint_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "mcp", Name = "test-tool" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("MCP tools require an endpoint")));
        }

        [TestMethod]
        public void Validate_NonMcpToolType_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "okf", Name = "test-tool", Endpoint = "https://example.test" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("invalid tool type")));
        }

        [TestMethod]
        public void Validate_DuplicateAgentNamesAcrossOrchestrators_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators.Add(new OrchestratorDefinition
            {
                Id = "other-orch",
                Name = "Other",
                Pattern = "sequential",
                Checkpointing = new CheckpointingDefinition(),
                Agents = [new AgentDefinition { Name = "test-agent", Instructions = "Another prompt", Provider = "azure", Model = "gpt-4o" }]
            });

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_ResponseFormatValidJsonSchema_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].ResponseFormat = new ResponseFormatDefinition
            {
                Type = "json_schema",
                Schema = """{"type":"object","properties":{"foo":{"type":"string"}}}"""
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_ResponseFormatInvalidType_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].ResponseFormat = new ResponseFormatDefinition { Type = "xml" };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("invalid responseFormat type")));
        }

        [TestMethod]
        public void Validate_ResponseFormatJsonSchemaMissingSchema_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].ResponseFormat = new ResponseFormatDefinition
            {
                Type = "json_schema"
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("requires a non-empty schema")));
        }

        [TestMethod]
        public void Validate_ResponseFormatJsonSchemaInvalidJson_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].ResponseFormat = new ResponseFormatDefinition
            {
                Type = "json_schema",
                Schema = "{not valid json"
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("not valid JSON")));
        }

        [TestMethod]
        public void Validate_ResponseFormatTextType_DoesNotRequireNameOrSchema()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].ResponseFormat = new ResponseFormatDefinition { Type = "text" };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_OrchestratorHumanInLoopEnabledWithCheckpointingDisabled_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Checkpointing = new CheckpointingDefinition
            {
                Enabled = false,
                HumanInLoop = new HumanInLoopDefinition { Enabled = true }
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("Orchestrator 'test-orch'") && e.Contains("humanInLoop is enabled but checkpointing is not")));
        }

        [TestMethod]
        public void Validate_OrchestratorHumanInLoopEnabledWithCheckpointingEnabled_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Checkpointing = new CheckpointingDefinition
            {
                Enabled = true,
                HumanInLoop = new HumanInLoopDefinition { Enabled = true }
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_EnableClarificationFlagWithHumanInLoopDisabled_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Checkpointing = new CheckpointingDefinition
            {
                Enabled = true,
                HumanInLoop = new HumanInLoopDefinition { Enabled = false, EnableClarificationFlag = true }
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("Orchestrator 'test-orch'") && e.Contains("enableClarificationFlag is set but humanInLoop.enabled is not")));
        }

        [TestMethod]
        public void Validate_EnableClarificationFlagWithHumanInLoopEnabled_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Checkpointing = new CheckpointingDefinition
            {
                Enabled = true,
                HumanInLoop = new HumanInLoopDefinition { Enabled = true, EnableClarificationFlag = true }
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_AgentMissingProviderAndModel_WithNoDefaultsConfigured_ReturnsErrors()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Provider = null;
            config.Orchestrators[0].Agents[0].Model = null;

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("provider is required")));
            Assert.IsTrue(result.Errors.Any(e => e.Contains("model is required")));
        }

        [TestMethod]
        public void Validate_AgentMissingProviderAndModel_WithDefaultsConfigured_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Provider = null;
            config.Orchestrators[0].Agents[0].Model = null;

            var validator = new ConfigValidator(new AgentDefaults { DefaultProvider = "azure", DefaultModel = "gpt-4o" });
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_WebSearchToolWithValidTavilyConfig_ReturnsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "test-key" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_WebSearchToolWithInvalidProvider_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "not-a-real-provider", ApiKey = "test-key" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("invalid provider")));
        }

        [TestMethod]
        public void Validate_WebSearchToolWithoutApiKey_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "tavily", ApiKey = "" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("apiKey is required")));
        }

        [TestMethod]
        public void Validate_WebSearchToolWithGoogleProviderMissingSearchEngineId_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Tools =
            [
                new ToolDefinition { Type = "web-search", Name = "web-search", Provider = "google", ApiKey = "test-key" }
            ];

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("searchEngineId")));
        }

        [TestMethod]
        public void Validate_AgentWithoutWebSearchTool_SkipsValidation()
        {
            var config = CreateValidConfig();

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_PlanningEnableTodoLoopWithoutEnableTodos_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Planning = new PlanningDefinition
            {
                EnableTodoLoop = true
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("enableTodoLoop requires enableTodos")));
        }

        [TestMethod]
        public void Validate_PlanningEnableTodoLoopWithEnableTodos_IsValid()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Planning = new PlanningDefinition
            {
                EnableTodos = true,
                EnableTodoLoop = true
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        [TestMethod]
        public void Validate_PlanningModeWithBlankInstructions_ReturnsError()
        {
            var config = CreateValidConfig();
            config.Orchestrators[0].Agents[0].Planning = new PlanningDefinition
            {
                EnableAgentMode = true,
                Modes = [new AgentModeDefinition { Name = "plan", Instructions = "  " }]
            };

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.Errors.Any(e => e.Contains("blank instructions")));
        }

        [TestMethod]
        public void Validate_AgentWithoutPlanning_SkipsValidation()
        {
            var config = CreateValidConfig();

            var validator = new ConfigValidator();
            var result = validator.Validate(config);

            Assert.IsTrue(result.IsValid);
        }

        private static PlatformConfig CreateValidConfig() => new()
        {
            Orchestrators =
            [
                new OrchestratorDefinition
                {
                    Id = "test-orch",
                    Name = "Test Orchestrator",
                    Pattern = "sequential",
                    Checkpointing = new CheckpointingDefinition(),
                    Agents = [new AgentDefinition { Name = "test-agent", Instructions = "You are helpful.", Provider = "azure", Model = "gpt-4o" }]
                }
            ]
        };
    }
}
