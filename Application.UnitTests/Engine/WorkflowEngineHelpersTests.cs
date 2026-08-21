using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Engine;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    /// <summary>
    /// Ported from Aura's <c>WorkflowEngineHelpersTests</c>, minus the
    /// <c>ResolveHandoffTargets</c> tests - the handoff pattern (and its target-resolution
    /// helper) was not migrated, since this service only supports the sequential pattern.
    /// </summary>
    [TestClass]
    public sealed partial class WorkflowEngineHelpersTests
    {
        [GeneratedRegex("[^0-9A-Za-z]+")]
        private static partial Regex NonAlphanumericRegex();

        [GeneratedRegex("[^0-9A-Za-z_]")]
        private static partial Regex NonAlphanumericOrUnderscoreRegex();

        [DataTestMethod]
        [DataRow("translator-en-fr")]
        [DataRow("triage agent")]
        [DataRow("SimpleName")]
        public void ComputeExecutorId_SanitizesNameAndId_MatchingMafConvention(string name)
        {
            // AIAgent.Id has no public setter (MAF assigns it internally), so we exercise the helper
            // against a real ChatClientAgent instance and independently recompute the expected
            // sanitized value from its actual (Name, Id) pair - this still validates the
            // concatenation order, separator, and character-sanitization behavior the helper must
            // reproduce to correctly correlate MAF's own generated executor IDs.
            var agent = CreateAgent(name);
            var expected = NonAlphanumericRegex().Replace($"{agent.Name}_{agent.Id}", "_");

            var executorId = WorkflowEngine.ComputeExecutorId(agent);

            Assert.AreEqual(expected, executorId);
            Assert.IsFalse(NonAlphanumericOrUnderscoreRegex().IsMatch(executorId));
        }

        [TestMethod]
        public void ComputeExecutorId_FallsBackToIdOnly_WhenNameIsNullOrEmpty()
        {
            var agent = CreateAgent(name: null);
            var expected = NonAlphanumericRegex().Replace(agent.Id, "_");

            var executorId = WorkflowEngine.ComputeExecutorId(agent);

            Assert.AreEqual(expected, executorId);
        }

        [TestMethod]
        public void ExtractFinalAssistantText_ReturnsLastAssistantMessage_FromFullConversationHistory()
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, "original question"),
                new(ChatRole.Assistant, "first agent reply"),
                new(ChatRole.Assistant, "final agent reply")
            };

            var result = WorkflowEngine.ExtractFinalAssistantText(messages);

            Assert.AreEqual("final agent reply", result);
        }

        [TestMethod]
        public void ExtractFinalAssistantText_FallsBackToExtractText_WhenNoAssistantMessagePresent()
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, "only user message") };

            var result = WorkflowEngine.ExtractFinalAssistantText(messages);

            Assert.AreEqual("only user message", result);
        }

        [TestMethod]
        public void ExtractFinalAssistantTextOrNull_ReturnsNull_WhenNoAssistantMessagePresent()
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, "forwarded input") };

            var result = WorkflowEngine.ExtractFinalAssistantTextOrNull(messages);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void ExtractText_ConcatenatesNonEmptyMessageText()
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.Assistant, "hello "),
                new(ChatRole.Assistant, string.Empty),
                new(ChatRole.Assistant, "world")
            };

            var result = WorkflowEngine.ExtractText(messages);

            Assert.AreEqual("hello world", result);
        }

        private static ChatClientAgent CreateAgent(string? name) =>
            new(new NoOpChatClient(), instructions: "test instructions", name: name);

        private sealed class NoOpChatClient : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }
    }
}
