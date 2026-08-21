namespace OpenAgentOrchestrator.Command.Application.Configuration
{
    /// <summary>
    /// Observability settings bound from the <c>Observability</c> configuration section.
    /// Controls the OpenTelemetry <see cref="System.Diagnostics.ActivitySource"/>/
    /// <see cref="System.Diagnostics.Metrics.Meter"/> name used by
    /// <see cref="OpenAgentOrchestrator.Command.Application.Agents.AgentFactory"/> when
    /// instrumenting chat/harness agents, and the service name reported to the OpenTelemetry
    /// Collector.
    /// </summary>
    /// <remarks>
    /// The OTLP exporter *endpoint* is deliberately not part of this options class - it is
    /// supplied exclusively via the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> /
    /// <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> environment variables (read natively by the
    /// OpenTelemetry SDK), so the Collector location is never hardcoded in source/config files
    /// and can vary freely between local, Docker Compose, and any future deployment target.
    /// </remarks>
    public sealed class ObservabilityOptions
    {
        /// <summary>Logical service name reported on all telemetry (resource attribute <c>service.name</c>).</summary>
        public string ServiceName { get; set; } = "OpenAgentOrchestrator";

        /// <summary>
        /// Name of the <see cref="System.Diagnostics.ActivitySource"/>/<see cref="System.Diagnostics.Metrics.Meter"/>
        /// used to instrument chat and Agent Harness agents. Must be registered via
        /// <c>AddSource</c>/<c>AddMeter</c> in <c>Program.cs</c> for spans/metrics to actually be
        /// collected.
        /// </summary>
        public string AgentSourceName { get; set; } = "OpenAgentOrchestrator.Agents";

        /// <summary>
        /// When <c>true</c>, prompts/responses are included in agent spans. Defaults to
        /// <c>false</c> (production-safe); enable only in development/test environments, per the
        /// Microsoft Agent Framework observability guidance, since prompts/responses may contain
        /// sensitive data.
        /// </summary>
        public bool EnableSensitiveData { get; set; }
    }
}
