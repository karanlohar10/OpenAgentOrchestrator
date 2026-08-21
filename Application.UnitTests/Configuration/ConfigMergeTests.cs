using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Configuration
{
    [TestClass]
    public sealed class ConfigMergeTests
    {
        private static PlatformConfig BuildConfig(
            string providerApiKey = "real-provider-key",
            string? toolClientSecret = "real-client-secret",
            string? headerValue = "real-header-value",
            string webSearchApiKey = "real-web-search-key")
        {
            return new PlatformConfig
            {
                Providers =
                [
                    new ProviderDefinition { Id = "Azure", Type = "azure-openai", Endpoint = "https://example.test", ApiKey = providerApiKey }
                ],
                Orchestrators =
                [
                    new OrchestratorDefinition
                    {
                        Id = "orch",
                        Name = "Orchestrator",
                        Pattern = "sequential",
                        Checkpointing = new CheckpointingDefinition(),
                        Agents =
                        [
                            new AgentDefinition
                            {
                                Name = "planner",
                                Instructions = "Plan.",
                                Provider = "Azure",
                                Model = "gpt-4o-mini",
                                Tools =
                                [
                                    new ToolDefinition
                                    {
                                        Type = "mcp",
                                        Name = "search-tool",
                                        Endpoint = "https://tool.test/mcp",
                                        AuthType = "bearer",
                                        TokenEndpoint = "https://tool.test/token",
                                        ClientId = "client-id",
                                        ClientSecret = toolClientSecret,
                                        Headers = headerValue is null ? null : new Dictionary<string, string> { ["X-API-Key"] = headerValue }
                                    }
                                ],
                                WebSearchTool = new WebSearchToolDefinition { Enabled = true, Provider = "tavily", ApiKey = webSearchApiKey }
                            }
                        ]
                    }
                ]
            };
        }

        [TestMethod]
        public void MergeSecrets_WhenProviderApiKeyIsRedactedPlaceholder_KeepsPreviousRealValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(providerApiKey: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Providers!.Single().ApiKey.Should().Be("real-provider-key");
        }

        [TestMethod]
        public void MergeSecrets_WhenProviderApiKeyIsBlank_KeepsPreviousRealValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(providerApiKey: "");
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Providers!.Single().ApiKey.Should().Be("real-provider-key");
        }

        [TestMethod]
        public void MergeSecrets_WhenProviderApiKeyIsRetyped_KeepsNewValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(providerApiKey: "brand-new-key");
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Providers!.Single().ApiKey.Should().Be("brand-new-key");
        }

        [TestMethod]
        public void MergeSecrets_WhenNewProviderHasRedactedApiKey_ReturnsError()
        {
            // Arrange
            var previous = new PlatformConfig { Providers = [], Orchestrators = BuildConfig().Orchestrators };
            var candidate = BuildConfig(providerApiKey: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().ContainSingle(e => e.Contains("Azure") && e.Contains("apiKey"));
        }

        [TestMethod]
        public void MergeSecrets_WhenToolClientSecretIsRedactedPlaceholder_KeepsPreviousRealValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(toolClientSecret: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Orchestrators.Single().Agents.Single().Tools!.Single().ClientSecret.Should().Be("real-client-secret");
        }

        [TestMethod]
        public void MergeSecrets_WhenToolHeaderValueIsRedactedPlaceholder_KeepsPreviousRealValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(headerValue: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Orchestrators.Single().Agents.Single().Tools!.Single().Headers!["X-API-Key"].Should().Be("real-header-value");
        }

        [TestMethod]
        public void MergeSecrets_WhenNewToolHasRedactedClientSecret_ReturnsError()
        {
            // Arrange
            var previous = BuildConfig();
            previous.Orchestrators.Single().Agents.Single().Tools!.Single().Name = "different-tool-name";
            var candidate = BuildConfig(toolClientSecret: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().Contain(e => e.Contains("search-tool") && e.Contains("clientSecret"));
        }

        [TestMethod]
        public void MergeSecrets_WhenWebSearchApiKeyIsRedactedPlaceholder_KeepsPreviousRealValue()
        {
            // Arrange
            var previous = BuildConfig();
            var candidate = BuildConfig(webSearchApiKey: ConfigRedaction.RedactedPlaceholder);
            var sut = new ConfigMerge();

            // Act
            var errors = sut.MergeSecrets(previous, candidate);

            // Assert
            errors.Should().BeEmpty();
            candidate.Orchestrators.Single().Agents.Single().WebSearchTool!.ApiKey.Should().Be("real-web-search-key");
        }
    }
}
