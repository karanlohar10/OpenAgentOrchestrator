namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Configures structured-output enforcement (Microsoft Agent Framework's
    /// <c>ChatOptions.ResponseFormat</c>) for an agent. The JSON Schema can either be authored
    /// inline in <see cref="Schema"/> or loaded from an external file via <see cref="SchemaFile"/>,
    /// which is resolved into <see cref="Schema"/> at config-parse time.
    /// </summary>
    public sealed class ResponseFormatDefinition
    {
        /// <summary>One of "json_schema", "json_object", or "text".</summary>
        public required string Type { get; set; }

        /// <summary>
        /// Raw JSON Schema text. Required when <see cref="Type"/> is "json_schema".
        /// </summary>
        public string? Schema { get; set; }

        /// <summary>
        /// Path (relative to <c>ConfigYaml.InstructionsRoot</c> - see <see cref="Configuration.ConfigYamlOptions"/>)
        /// to a JSON file whose contents are used as <see cref="Schema"/> when <see cref="Schema"/>
        /// isn't set inline. Resolved once at config-load time by <c>ConfigStore</c>. Ignored
        /// (with a warning) if <see cref="Schema"/> is also set.
        /// </summary>
        public string? SchemaFile { get; set; }
    }
}
