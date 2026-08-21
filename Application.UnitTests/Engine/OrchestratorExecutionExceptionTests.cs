using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Engine;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    /// <summary>
    /// Verifies that <see cref="OrchestratorExecutionException.Classify"/> maps exception types
    /// raised during orchestrator execution/resume to the correct <see
    /// cref="OrchestratorErrorCategory"/>, which in turn drives the HTTP status code returned by
    /// <c>OrchestratorSessionsCommandController</c> instead of always returning 200 OK.
    /// </summary>
    [TestClass]
    public sealed class OrchestratorExecutionExceptionTests
    {
        [DataTestMethod]
        [DataRow(typeof(ArgumentException))]
        [DataRow(typeof(ArgumentNullException))]
        public void Classify_ReturnsConfiguration_ForArgumentExceptions(Type exceptionType)
        {
            var ex = (Exception)Activator.CreateInstance(exceptionType, "paramName")!;

            var category = OrchestratorExecutionException.Classify(ex);

            Assert.AreEqual(OrchestratorErrorCategory.Configuration, category);
        }

        [TestMethod]
        public void Classify_ReturnsUpstreamDependency_ForHttpRequestException()
        {
            var ex = new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).");

            var category = OrchestratorExecutionException.Classify(ex);

            Assert.AreEqual(OrchestratorErrorCategory.UpstreamDependency, category);
        }

        [TestMethod]
        public void Classify_ReturnsUnexpected_ForOtherExceptions()
        {
            var ex = new InvalidOperationException("something else went wrong");

            var category = OrchestratorExecutionException.Classify(ex);

            Assert.AreEqual(OrchestratorErrorCategory.Unexpected, category);
        }

        [TestMethod]
        public void Constructor_PreservesSessionIdCategoryMessageAndInnerException()
        {
            var inner = new HttpRequestException("401");
            var ex = new OrchestratorExecutionException("session-123", OrchestratorErrorCategory.UpstreamDependency, "failed calling MCP tool", inner);

            Assert.AreEqual("session-123", ex.SessionId);
            Assert.AreEqual(OrchestratorErrorCategory.UpstreamDependency, ex.Category);
            Assert.AreEqual("failed calling MCP tool", ex.Message);
            Assert.AreSame(inner, ex.InnerException);
        }
    }
}
