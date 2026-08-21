using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Configuration;

namespace OpenAgentOrchestrator.Application.UnitTests.Configuration
{
    [TestClass]
    public sealed class ObservabilityOptionsTests
    {
        [TestMethod]
        public void Defaults_AreProductionSafe_WhenNoConfigurationIsProvided()
        {
            // Arrange
            var options = new ObservabilityOptions();

            // Assert
            options.ServiceName.Should().Be("OpenAgentOrchestrator");
            options.AgentSourceName.Should().Be("OpenAgentOrchestrator.Agents");
            options.EnableSensitiveData.Should().BeFalse("prompts/responses must not be captured by default");
        }

        [TestMethod]
        public void Binds_FromObservabilityConfigurationSection()
        {
            // Arrange
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Observability:ServiceName"] = "CustomService",
                    ["Observability:AgentSourceName"] = "Custom.Agents",
                    ["Observability:EnableSensitiveData"] = "true"
                })
                .Build();

            var services = new ServiceCollection();
            services.Configure<ObservabilityOptions>(configuration.GetSection("Observability"));

            // Act
            var options = services.BuildServiceProvider()
                .GetRequiredService<IOptions<ObservabilityOptions>>().Value;

            // Assert
            options.ServiceName.Should().Be("CustomService");
            options.AgentSourceName.Should().Be("Custom.Agents");
            options.EnableSensitiveData.Should().BeTrue();
        }
    }
}
