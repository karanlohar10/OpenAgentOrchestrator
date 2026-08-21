using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Sessions;

namespace OpenAgentOrchestrator.Application.UnitTests.Sessions
{
    [TestClass]
    public sealed class SessionAndStoreTests
    {
        [TestMethod]
        public void OrchestratorSession_DefaultsMatchExpectedState()
        {
            // Arrange
            var session = new OrchestratorSession { OrchestratorId = "orch" };

            // Assert
            session.SessionId.Should().NotBeNullOrWhiteSpace();
            session.Status.Should().Be("running");
            session.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, precision: TimeSpan.FromSeconds(5));
            session.Steps.Should().BeEmpty();
        }

        [TestMethod]
        public void InMemorySessionStore_CreateAndGet_ReturnsSameSession()
        {
            // Arrange
            var sut = new InMemorySessionStore();

            // Act
            var created = sut.Create("orch");
            var loaded = sut.Get(created.SessionId);

            // Assert
            loaded.Should().BeSameAs(created);
            loaded!.OrchestratorId.Should().Be("orch");
        }

        [TestMethod]
        public void InMemorySessionStore_GetUnknownSession_ReturnsNull()
        {
            var sut = new InMemorySessionStore();

            var session = sut.Get("missing");

            session.Should().BeNull();
        }
    }
}
