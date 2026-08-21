using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenAgentOrchestrator.Command.Application.Agents
{
    /// <summary>
    /// Wraps a chat client with retry-with-backoff behavior for HTTP 429 (rate limit) errors.
    /// Applies to the Azure OpenAI provider by inspecting the exception shape it throws.
    /// </summary>
    public sealed class RetryingChatClient : DelegatingChatClient
    {
        private const int DefaultMaxAttempts = 5;
        private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

        private readonly int _maxAttempts;
        private readonly TimeSpan _baseDelay;
        private readonly ILogger? _logger;

        public RetryingChatClient(
            IChatClient innerClient,
            int maxAttempts = DefaultMaxAttempts,
            TimeSpan? baseDelay = null,
            ILogger? logger = null)
            : base(innerClient)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
            _maxAttempts = maxAttempts;
            _baseDelay = baseDelay ?? DefaultBaseDelay;
            _logger = logger;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = 0;

            while (true)
            {
                try
                {
                    return await base.GetResponseAsync(messages, options, cancellationToken);
                }
                catch (Exception ex) when (attempt < _maxAttempts - 1 && IsRateLimitError(ex))
                {
                    attempt++;
                    var delay = GetRetryDelay(ex, attempt);

                    _logger?.LogWarning(
                        ex,
                        "Rate limited (HTTP 429). Retry attempt {Attempt}/{MaxAttempts} in {DelayMs}ms.",
                        attempt, _maxAttempts, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        internal static bool IsRateLimitError(Exception ex) => ex switch
        {
            ClientResultException cre => cre.Status == (int)HttpStatusCode.TooManyRequests,
            HttpRequestException hre => hre.StatusCode == HttpStatusCode.TooManyRequests,
            _ => ex.Message.Contains("429", StringComparison.Ordinal)
                 || ex.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase)
        };

        private TimeSpan GetRetryDelay(Exception ex, int attempt)
        {
            var retryAfter = TryGetRetryAfter(ex);
            if (retryAfter is not null)
                return retryAfter.Value;

            // Exponential backoff with jitter: baseDelay * 2^(attempt-1) + random jitter, capped.
            var exponential = _baseDelay * Math.Pow(2, attempt - 1);
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            var delay = exponential + jitter;

            return delay > MaxDelay ? MaxDelay : delay;
        }

        private static TimeSpan? TryGetRetryAfter(Exception ex)
        {
            if (ex is not ClientResultException cre)
                return null;

            try
            {
                var response = cre.GetRawResponse();
                if (response is not null
                    && response.Headers.TryGetValue("Retry-After", out var value)
                    && int.TryParse(value, out var seconds) && seconds >= 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
            catch
            {
                // Best-effort only — fall back to exponential backoff if headers are unavailable.
            }

            return null;
        }
    }
}
