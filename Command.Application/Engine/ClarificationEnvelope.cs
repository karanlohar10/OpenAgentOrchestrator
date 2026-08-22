using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// The fixed JSON shape agents are instructed to respond with when their orchestrator has
    /// <c>checkpointing.humanInLoop.enableClarificationFlag: true</c> (see
    /// <see cref="OpenAgentOrchestrator.Command.Application.Agents.AgentFactory.ClarificationEnvelopeInstructions"/>):
    /// <c>{ "needsClarification": bool, "clarificationQuestion": string?, "content": string }</c>.
    /// Parsed by <see cref="StepReviewExecutor"/> to decide whether a paused step's answer should
    /// loop back to the same agent (a genuine question) or forward to the next one (a routine
    /// result).
    /// </summary>
    internal sealed record ClarificationEnvelope(bool NeedsClarification, string? ClarificationQuestion, string? Content)
    {
        /// <summary>
        /// Tolerantly parses <paramref name="text"/> as a <see cref="ClarificationEnvelope"/>.
        /// Accepts the JSON object optionally wrapped in a markdown code fence (some models add one
        /// despite instructions not to). Returns <see langword="false"/> (rather than throwing) on
        /// any malformed/missing-required-field input, so callers can fail safe.
        /// </summary>
        public static bool TryParse(string text, out ClarificationEnvelope envelope)
        {
            envelope = new ClarificationEnvelope(false, null, null);

            var trimmed = StripMarkdownCodeFence(text);
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            try
            {
                var dto = JsonSerializer.Deserialize<EnvelopeDto>(trimmed, JsonOptions);
                if (dto is null || dto.NeedsClarification is null)
                    return false;

                envelope = new ClarificationEnvelope(dto.NeedsClarification.Value, dto.ClarificationQuestion, dto.Content);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string StripMarkdownCodeFence(string text)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;

            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0)
                return trimmed;

            var withoutOpeningFence = trimmed[(firstNewline + 1)..];
            var closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
            return closingFenceIndex >= 0 ? withoutOpeningFence[..closingFenceIndex].Trim() : withoutOpeningFence.Trim();
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private sealed class EnvelopeDto
        {
            [JsonPropertyName("needsClarification")]
            public bool? NeedsClarification { get; set; }

            [JsonPropertyName("clarificationQuestion")]
            public string? ClarificationQuestion { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}
