using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OpenAgentOrchestrator.Application.UnitTests.TestHelpers
{
    internal sealed class RecordingChatClient(
        Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, string> responseFactory) : IChatClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, string> _responseFactory = responseFactory;

        public List<IReadOnlyList<ChatMessage>> MessagesByCall { get; } = [];

        public List<ChatOptions?> OptionsByCall { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var responseText = RecordAndCreateResponse(messages, options, cancellationToken);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var responseText = RecordAndCreateResponse(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, responseText);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private string RecordAndCreateResponse(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken cancellationToken)
        {
            var messageList = messages.ToList();
            MessagesByCall.Add(messageList);
            OptionsByCall.Add(options);
            return _responseFactory(messageList, options, cancellationToken);
        }
    }

    internal static class WorkflowTestDoubles
    {
        public static ChatClientAgent CreateAgent(string name, RecordingChatClient chatClient, string? instructions = null) =>
            new(chatClient, new ChatClientAgentOptions
            {
                Id = name,
                Name = name,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions ?? $"Instructions for {name}"
                }
            });

        public static string GetLatestUserText(IReadOnlyList<ChatMessage> messages) =>
            messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
    }

    internal sealed class TestArtifactDirectory : IDisposable
    {
        public TestArtifactDirectory(string name)
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "TestArtifacts",
                $"{name}-{Guid.NewGuid():N}"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
