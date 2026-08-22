using System.Text;
using System.Text.Json;

namespace OpenAgentOrchestrator.Command.Application.Engine
{
    /// <summary>
    /// The clarification signal an agent's JSON response carries when its orchestrator has
    /// <c>checkpointing.humanInLoop.enableClarificationFlag: true</c>. Two shapes are supported,
    /// depending on whether the agent has its own <c>responseFormat: json_schema</c> (see
    /// <see cref="OpenAgentOrchestrator.Command.Application.Agents.AgentFactory"/>):
    /// <list type="bullet">
    /// <item>No agent schema (free-text contract, <c>ClarificationEnvelopeInstructions</c>): a flat
    /// envelope <c>{ "needsClarification": bool, "clarificationQuestion": string?, "content": string }</c>
    /// where <c>content</c> is an explicit top-level string holding the agent's real output.</item>
    /// <item>Agent has its own <c>json_schema</c> (<c>ClarificationEnvelopeInstructionsForStructuredAgent</c>,
    /// <c>MergeClarificationProperties</c>): <c>needsClarification</c>/<c>clarificationQuestion</c>
    /// are merged as additive sibling properties directly into the agent's own declared schema, so
    /// there is no separate <c>content</c> key - the agent's own fields sit at the top level next to
    /// the two signal fields. In this shape, <see cref="Content"/> is reconstructed by stripping the
    /// two signal keys back out and re-serializing the remainder, so it is byte-for-byte what the
    /// agent's own schema would have produced without the merge.
    /// </list>
    /// Parsed by <see cref="StepReviewExecutor"/> to decide whether a paused step's answer should
    /// loop back to the same agent (a genuine question) or forward to the next one (a routine
    /// result).
    /// </summary>
    internal sealed record ClarificationEnvelope(bool NeedsClarification, string? ClarificationQuestion, string? Content)
    {
        internal const string NeedsClarificationPropertyName = "needsClarification";
        internal const string ClarificationQuestionPropertyName = "clarificationQuestion";
        private const string ContentPropertyName = "content";

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
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                if (!TryGetPropertyCaseInsensitive(root, NeedsClarificationPropertyName, out var needsClarificationProp)
                    || needsClarificationProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                var needsClarification = needsClarificationProp.GetBoolean();

                var clarificationQuestion = TryGetPropertyCaseInsensitive(root, ClarificationQuestionPropertyName, out var questionProp)
                    && questionProp.ValueKind == JsonValueKind.String
                        ? questionProp.GetString()
                        : null;

                string? content;
                if (TryGetPropertyCaseInsensitive(root, ContentPropertyName, out var contentProp))
                {
                    // Flat free-text-agent contract - "content" is an explicit top-level string.
                    content = contentProp.ValueKind == JsonValueKind.String ? contentProp.GetString() : contentProp.GetRawText();
                }
                else
                {
                    // Schema-merged agent contract - reconstruct "content" as the agent's own
                    // schema shape by stripping the two clarification signal keys back out.
                    content = SerializeWithoutClarificationKeys(root);
                }

                envelope = new ClarificationEnvelope(needsClarification, clarificationQuestion, content);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string SerializeWithoutClarificationKeys(JsonElement root)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, NeedsClarificationPropertyName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, ClarificationQuestionPropertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
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
    }
}
