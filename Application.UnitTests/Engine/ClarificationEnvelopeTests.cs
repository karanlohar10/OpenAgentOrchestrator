using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenAgentOrchestrator.Command.Application.Engine;

namespace OpenAgentOrchestrator.Application.UnitTests.Engine
{
    [TestClass]
    public sealed class ClarificationEnvelopeTests
    {
        [TestMethod]
        public void TryParse_FlatFreeTextEnvelope_WithExplicitContentKey_ParsesAsIs()
        {
            // Arrange - the flat free-text-agent contract (no agent responseFormat schema).
            var text = """{"needsClarification": true, "clarificationQuestion": "Which framing?", "content": "partial progress"}""";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out var envelope);

            // Assert
            parsed.Should().BeTrue();
            envelope.NeedsClarification.Should().BeTrue();
            envelope.ClarificationQuestion.Should().Be("Which framing?");
            envelope.Content.Should().Be("partial progress");
        }

        [TestMethod]
        public void TryParse_SchemaMergedEnvelope_WithoutContentKey_ReconstructsContentByStrippingSignalKeys()
        {
            // Arrange - the schema-merged-agent contract: needsClarification/clarificationQuestion
            // sit as siblings alongside the agent's own declared fields, with no "content" key.
            var text = """{"summary": "quantum computing status", "confidence": "high", "needsClarification": false, "clarificationQuestion": null}""";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out var envelope);

            // Assert
            parsed.Should().BeTrue();
            envelope.NeedsClarification.Should().BeFalse();
            envelope.ClarificationQuestion.Should().BeNull();
            // Content is reconstructed as exactly the agent's own schema shape - the two signal
            // keys are gone, nothing else changed.
            envelope.Content.Should().NotContain("needsClarification");
            envelope.Content.Should().NotContain("clarificationQuestion");
            envelope.Content.Should().Contain("\"summary\"");
            envelope.Content.Should().Contain("quantum computing status");
            envelope.Content.Should().Contain("\"confidence\"");
        }

        [TestMethod]
        public void TryParse_SchemaMergedEnvelope_WhenNeedsClarificationTrue_StillReconstructsRemainingFields()
        {
            // Arrange
            var text = """{"summary": "", "confidence": "low", "needsClarification": true, "clarificationQuestion": "Which framing do you want?"}""";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out var envelope);

            // Assert
            parsed.Should().BeTrue();
            envelope.NeedsClarification.Should().BeTrue();
            envelope.ClarificationQuestion.Should().Be("Which framing do you want?");
            envelope.Content.Should().NotContain("needsClarification");
            envelope.Content.Should().Contain("\"confidence\"");
        }

        [TestMethod]
        public void TryParse_HandlesMarkdownCodeFence()
        {
            // Arrange
            var text = "```json\n{\"needsClarification\": false, \"content\": \"done\"}\n```";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out var envelope);

            // Assert
            parsed.Should().BeTrue();
            envelope.NeedsClarification.Should().BeFalse();
            envelope.Content.Should().Be("done");
        }

        [TestMethod]
        public void TryParse_MissingNeedsClarificationField_FailsSafe()
        {
            // Arrange
            var text = """{"content": "just a result, not an envelope"}""";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out _);

            // Assert
            parsed.Should().BeFalse();
        }

        [TestMethod]
        public void TryParse_MalformedJson_FailsSafe()
        {
            // Arrange
            var text = "this is not json at all { needsClarification";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out _);

            // Assert
            parsed.Should().BeFalse();
        }

        [TestMethod]
        public void TryParse_NonObjectJson_FailsSafe()
        {
            // Arrange
            var text = "[1, 2, 3]";

            // Act
            var parsed = ClarificationEnvelope.TryParse(text, out _);

            // Assert
            parsed.Should().BeFalse();
        }

        [TestMethod]
        public void TryParse_EmptyString_FailsSafe()
        {
            // Act
            var parsed = ClarificationEnvelope.TryParse("   ", out _);

            // Assert
            parsed.Should().BeFalse();
        }
    }
}
