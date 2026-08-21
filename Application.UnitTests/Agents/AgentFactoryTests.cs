using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenAgentOrchestrator.Application.UnitTests.TestHelpers;
using OpenAgentOrchestrator.Command.Application.Agents;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Command.Application.Engine;
using OpenAgentOrchestrator.Command.Application.ToolBinding;
using OpenAgentOrchestrator.Command.Application.Tools;
using OpenAgentOrchestrator.Command.Application.Tools.WebSearch;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Agents
{
    [TestClass]
    public sealed class AgentFactoryTests
    {
        [TestMethod]
        public async Task CreateAgentAsync_UsesDefaultProviderAndInstructions_WhenAgentOverridesAreMissing()
        {
            // Arrange
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            var toolBinderFactory = new Mock<IToolBinderFactory>();
            ProviderDefinition? usedProvider = null;
            string? usedModel = null;

            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Callback<ProviderDefinition, string>((provider, model) =>
                {
                    usedProvider = provider;
                    usedModel = model;
                })
                .Returns(recordingClient);

            var sut = new AgentFactory(
                chatClientFactory.Object,
                toolBinderFactory.Object,
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "planner",
                Instructions = "Plan the work.",
                Provider = "azure",
                Model = "gpt-4o-mini"
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);

            // Assert
            // The returned agent is instrumented with agent-level OpenTelemetry (see
            // AgentFactory.CreateChatAgent), so it is no longer a bare ChatClientAgent - it is
            // a DelegatingAIAgent wrapper around one. Name/Id are still proxied through from the
            // inner ChatClientAgent, which is what matters for ExecutorId stability.
            agent.Name.Should().Be("planner");
            agent.Id.Should().Be("planner");
            usedProvider!.Id.Should().Be("azure");
            usedModel.Should().Be("gpt-4o-mini");

            await RunAgentAsync(agent, "hello");
            recordingClient.OptionsByCall.Should().ContainSingle();
            recordingClient.OptionsByCall[0]!.Instructions.Should().Be("Plan the work.");
        }

        [TestMethod]
        public async Task CreateAgentAsync_BindsToolsAndBuildsJsonSchemaResponseFormat()
        {
            // Arrange
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(recordingClient);

            var tool = AIFunctionFactory.Create(
                (Func<string>)(() => "ok"),
                name: "lookup",
                description: "Looks things up",
                serializerOptions: null);

            var toolBinderFactory = new Mock<IToolBinderFactory>();
            toolBinderFactory
                .Setup(factory => factory.BindToolsAsync(It.IsAny<IEnumerable<ToolDefinition>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([tool]);

            var sut = new AgentFactory(
                chatClientFactory.Object,
                toolBinderFactory.Object,
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "structured-agent",
                Instructions = "Return structured output.",
                Provider = "openai-secondary",
                Model = "gpt-5-mini",
                Tools = [new ToolDefinition { Type = "mcp", Name = "lookup", Endpoint = "https://example.test" }],
                ResponseFormat = new ResponseFormatDefinition
                {
                    Type = "json_schema",
                    Schema = """{"type":"object","properties":{"answer":{"type":"string"}}}"""
                }
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);

            await RunAgentAsync(agent, "hello");

            // Assert
            recordingClient.OptionsByCall.Should().ContainSingle();
            var options = recordingClient.OptionsByCall[0];
            options.Should().NotBeNull();
            options!.Tools.Should().ContainSingle()
                .Which.Name.Should().Be("lookup");
            options.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenInstructionsAreMissing_ThrowsInvalidOperationException()
        {
            // Arrange
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(new RecordingChatClient((_, _, _) => "unused"));

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            // Act
            var action = () => sut.CreateAgentAsync(
                new AgentDefinition { Name = "planner", Provider = "azure", Model = "gpt-4o" });

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenProviderIsUndefined_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new AgentFactory(
                Mock.Of<IChatClientFactory>(),
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            // Act
            var action = () => sut.CreateAgentAsync(
                new AgentDefinition
                {
                    Name = "planner",
                    Instructions = "Plan.",
                    Provider = "missing-provider",
                    Model = "gpt-4o"
                });

            // Assert
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            exception.Message.Should().Contain("missing-provider");
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenJsonSchemaIsInvalid_ThrowsInvalidOperationException()
        {
            // Arrange
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(new RecordingChatClient((_, _, _) => "unused"));

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            // Act
            var action = () => sut.CreateAgentAsync(
                new AgentDefinition
                {
                    Name = "planner",
                    Instructions = "Plan.",
                    Provider = "azure",
                    Model = "gpt-4o",
                    ResponseFormat = new ResponseFormatDefinition
                    {
                        Type = "json_schema",
                        Schema = "{ not json"
                    }
                });

            // Assert
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            exception.Message.Should().Contain("not valid JSON");
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenProviderAndModelAreMissing_FallsBackToAgentDefaults()
        {
            // Arrange
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            ProviderDefinition? usedProvider = null;
            string? usedModel = null;

            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Callback<ProviderDefinition, string>((provider, model) =>
                {
                    usedProvider = provider;
                    usedModel = model;
                })
                .Returns(recordingClient);

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(defaultProvider: "azure", defaultModel: "gpt-4o-mini"),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "planner",
                Instructions = "Plan the work."
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);

            // Assert
            usedProvider!.Id.Should().Be("azure");
            usedModel.Should().Be("gpt-4o-mini");
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenProviderIsMissingAndNoDefaultConfigured_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new AgentFactory(
                Mock.Of<IChatClientFactory>(),
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            // Act
            var action = () => sut.CreateAgentAsync(
                new AgentDefinition { Name = "planner", Instructions = "Plan.", Model = "gpt-4o" });

            // Assert
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            exception.Message.Should().Contain("DefaultProvider");
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenModelIsMissingAndNoDefaultConfigured_ThrowsInvalidOperationException()
        {
            // Arrange
            var sut = new AgentFactory(
                Mock.Of<IChatClientFactory>(),
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                Mock.Of<IWebSearchToolFactory>(),
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            // Act
            var action = () => sut.CreateAgentAsync(
                new AgentDefinition { Name = "planner", Instructions = "Plan.", Provider = "azure" });

            // Assert
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            exception.Message.Should().Contain("DefaultModel");
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenWebSearchToolEnabled_AttachesToolFromFactory()
        {
            // Arrange
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(recordingClient);

            var webSearchTool = AIFunctionFactory.Create(
                (Func<string>)(() => "results"),
                name: "web_search",
                description: "Searches the web",
                serializerOptions: null);

            var webSearchToolFactory = new Mock<IWebSearchToolFactory>();
            webSearchToolFactory
                .Setup(factory => factory.Create(It.IsAny<WebSearchToolDefinition>()))
                .Returns(webSearchTool);

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                webSearchToolFactory.Object,
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "research-agent",
                Instructions = "Research things.",
                Provider = "azure",
                Model = "gpt-4o-mini",
                WebSearchTool = new WebSearchToolDefinition { Enabled = true, Provider = "tavily", ApiKey = "test-key" }
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);
            await RunAgentAsync(agent, "hello");

            // Assert
            recordingClient.OptionsByCall.Should().ContainSingle();
            recordingClient.OptionsByCall[0]!.Tools.Should().ContainSingle()
                .Which.Name.Should().Be("web_search");
            webSearchToolFactory.Verify(factory => factory.Create(agentDefinition.WebSearchTool), Times.Once);
        }

        [TestMethod]
        public async Task CreateAgentAsync_WhenWebSearchToolDisabled_DoesNotAttachTool()
        {
            // Arrange
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(recordingClient);

            var webSearchToolFactory = new Mock<IWebSearchToolFactory>();

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                webSearchToolFactory.Object,
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "planner",
                Instructions = "Plan the work.",
                Provider = "azure",
                Model = "gpt-4o-mini"
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);
            await RunAgentAsync(agent, "hello");

            // Assert
            recordingClient.OptionsByCall.Should().ContainSingle();
            recordingClient.OptionsByCall[0]!.Tools.Should().BeNull();
            webSearchToolFactory.Verify(factory => factory.Create(It.IsAny<WebSearchToolDefinition>()), Times.Never);
        }

        [TestMethod]
        public async Task CreateAgentAsync_HarnessAgentWithDisableWebSearchAndWebSearchTool_CreatesSuccessfully()
        {
            // Arrange - smoke test: harness agent creation must not throw when DisableWebSearch
            // is set alongside a custom webSearchTool (the combination the docs recommend to
            // avoid the agent receiving both a hosted and a custom search tool at once).
            var recordingClient = new RecordingChatClient((messages, _, _) =>
                $"answer:{WorkflowTestDoubles.GetLatestUserText(messages)}");
            var chatClientFactory = new Mock<IChatClientFactory>();
            chatClientFactory
                .Setup(factory => factory.Create(It.IsAny<ProviderDefinition>(), It.IsAny<string>()))
                .Returns(recordingClient);

            var webSearchTool = AIFunctionFactory.Create(
                (Func<string>)(() => "results"),
                name: "web_search",
                description: "Searches the web",
                serializerOptions: null);

            var webSearchToolFactory = new Mock<IWebSearchToolFactory>();
            webSearchToolFactory
                .Setup(factory => factory.Create(It.IsAny<WebSearchToolDefinition>()))
                .Returns(webSearchTool);

            var sut = new AgentFactory(
                chatClientFactory.Object,
                Mock.Of<IToolBinderFactory>(),
                Mock.Of<IShellToolFactory>(),
                webSearchToolFactory.Object,
                CreateConfigStore(),
                CreateAgentDefaults(),
                CreateObservabilityOptions(),
                NullLogger<AgentFactory>.Instance);

            var agentDefinition = new AgentDefinition
            {
                Name = "research-agent",
                Instructions = "Research things.",
                Provider = "azure",
                Model = "gpt-4o-mini",
                AgentType = "harness",
                Harness = new HarnessOptionsDefinition { DisableWebSearch = true },
                WebSearchTool = new WebSearchToolDefinition { Enabled = true, Provider = "tavily", ApiKey = "test-key" }
            };

            // Act
            var agent = await sut.CreateAgentAsync(agentDefinition);

            // Assert
            agent.Should().NotBeNull();
            agent.Name.Should().Be("research-agent");
        }

        private static IConfigStore CreateConfigStore()
        {
            var config = new PlatformConfig
            {
                Providers =
                [
                    new ProviderDefinition { Id = "azure", Type = "azure-openai", Endpoint = "https://example.openai.azure.com", ApiKey = "key" },
                    new ProviderDefinition { Id = "openai-secondary", Type = "azure-openai", Endpoint = "https://example2.openai.azure.com", ApiKey = "key" }
                ],
                Orchestrators =
                [
                    new OrchestratorDefinition
                    {
                        Id = "orch",
                        Name = "Orchestrator",
                        Pattern = "sequential",
                        Checkpointing = new CheckpointingDefinition(),
                        Agents = [new AgentDefinition { Name = "planner", Instructions = "Plan.", Provider = "azure", Model = "gpt-4o-mini" }]
                    }
                ]
            };

            var mock = new Mock<IConfigStore>();
            mock.Setup(s => s.GetConfig()).Returns(config);
            mock.Setup(s => s.GetProvider(It.IsAny<string>()))
                .Returns<string>(id => config.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            mock.Setup(s => s.GetOrchestrator(It.IsAny<string>()))
                .Returns<string>(id => config.Orchestrators.FirstOrDefault(o => o.Id == id));
            mock.Setup(s => s.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(config);
            mock.Setup(s => s.GetProviderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((id, _) => Task.FromResult(config.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));
            mock.Setup(s => s.GetOrchestratorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((id, _) => Task.FromResult(config.Orchestrators.FirstOrDefault(o => o.Id == id)));
            return mock.Object;
        }

        private static IOptions<AgentDefaults> CreateAgentDefaults(string? defaultProvider = null, string? defaultModel = null) =>
            Options.Create(new AgentDefaults { DefaultProvider = defaultProvider, DefaultModel = defaultModel });

        private static IOptions<ObservabilityOptions> CreateObservabilityOptions() =>
            Options.Create(new ObservabilityOptions());

        private static async Task RunAgentAsync(AIAgent agent, string message)
        {
            var session = await agent.CreateSessionAsync();
            await agent.RunAsync(message, session, options: null, cancellationToken: default);
        }
    }
}

