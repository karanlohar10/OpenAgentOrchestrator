using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Agents;

namespace OpenAgentOrchestrator.Application.UnitTests.Agents
{
    [TestClass]
    public sealed class RetryingChatClientTests
    {
        [TestMethod]
        public async Task GetResponseAsync_WhenRateLimited_RetriesUntilSuccess()
        {
            // Arrange
            var innerClient = new SequenceChatClient(
            [
                () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests),
                () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests),
                () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            ]);
            var sut = new RetryingChatClient(innerClient, maxAttempts: 3, baseDelay: TimeSpan.Zero);

            // Act
            var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

            // Assert
            response.Text.Should().Be("done");
            innerClient.AttemptCount.Should().Be(3);
        }

        [TestMethod]
        public async Task GetResponseAsync_WhenRateLimitPersists_ThrowsAfterMaxAttempts()
        {
            // Arrange
            var innerClient = new SequenceChatClient(
            [
                () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests),
                () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests)
            ]);
            var sut = new RetryingChatClient(innerClient, maxAttempts: 2, baseDelay: TimeSpan.Zero);

            // Act
            var action = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

            // Assert
            await Assert.ThrowsExactlyAsync<HttpRequestException>(action);
            innerClient.AttemptCount.Should().Be(2);
        }

        [TestMethod]
        public async Task GetResponseAsync_WhenErrorIsNotRateLimit_DoesNotRetry()
        {
            // Arrange
            var innerClient = new SequenceChatClient(
            [
                () => throw new InvalidOperationException("boom")
            ]);
            var sut = new RetryingChatClient(innerClient, maxAttempts: 5, baseDelay: TimeSpan.Zero);

            // Act
            var action = () => sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

            // Assert
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(action);
            innerClient.AttemptCount.Should().Be(1);
        }

        [DataTestMethod]
        [DataRow("429 too many requests", true)]
        [DataRow("rate_limit exceeded", true)]
        [DataRow("too_many_requests", true)]
        [DataRow("different error", false)]
        public void IsRateLimitError_MessagePatterns_ReturnsExpectedResult(string message, bool expected)
        {
            var exception = new Exception(message);

            var result = RetryingChatClient.IsRateLimitError(exception);

            result.Should().Be(expected);
        }

        [DataTestMethod]
        [DataRow(System.Net.HttpStatusCode.TooManyRequests, true)]
        [DataRow(System.Net.HttpStatusCode.BadRequest, false)]
        public void IsRateLimitError_HttpRequestExceptionStatusCodes_ReturnExpectedResult(System.Net.HttpStatusCode statusCode, bool expected)
        {
            var exception = new HttpRequestException("status", null, statusCode);

            var result = RetryingChatClient.IsRateLimitError(exception);

            result.Should().Be(expected);
        }

        [TestMethod]
        public void Constructor_WhenMaxAttemptsIsLessThanOne_ThrowsArgumentOutOfRangeException()
        {
            var action = () => new RetryingChatClient(
                new SequenceChatClient([() => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))]),
                maxAttempts: 0);

            action.Should().Throw<ArgumentOutOfRangeException>();
        }

        private sealed class SequenceChatClient(IEnumerable<Func<ChatResponse>> responses) : IChatClient
        {
            private readonly Queue<Func<ChatResponse>> _responses = new(responses);

            public int AttemptCount { get; private set; }

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                AttemptCount++;
                return Task.FromResult(_responses.Dequeue().Invoke());
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
