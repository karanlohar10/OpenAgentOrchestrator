using Asp.Versioning;
using OpenAgentOrchestrator.Command.Application;
using OpenAgentOrchestrator.Command.Application.Configuration;
using OpenAgentOrchestrator.Query.Application;
using OpenAgentOrchestrator.Service.Swagger;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
}).AddMvc();

builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<ExecuteRequestOperationFilter>();
});

builder.Services.AddHealthChecks();

// Command/Query application services: config.yaml loading, session/checkpoint stores, tool
// binding, agent creation (chat + Agent Harness + Shell Tools), and the workflow engine.
builder.Services.AddCommandApplication(builder.Configuration);
builder.Services.AddQueryApplication();

// --- Observability (OpenTelemetry: traces, metrics, logs) ---------------------------------
//
// The OTLP exporter endpoint/protocol are deliberately never hardcoded here - AddOtlpExporter()
// with no explicit Endpoint reads the standard OTEL_EXPORTER_OTLP_ENDPOINT /
// OTEL_EXPORTER_OTLP_PROTOCOL environment variables natively. Locally (no Collector running)
// this simply means the exporter has nowhere to send to; its batch processor swallows/retries
// export failures internally and never throws into request-handling code, so the app keeps
// working whether or not a Collector is reachable.
var observability = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>()
    ?? new ObservabilityOptions();

// Chat/Harness agent spans use OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT to decide
// whether prompts/responses (potentially sensitive) are included. Microsoft.Agents.AI.Harness's
// HarnessAgentOptions has no dedicated sensitive-data flag, so this env var is the single
// process-wide switch that covers both the plain chat-agent (.UseOpenTelemetry configure
// callback, see AgentFactory) and the Harness path consistently.
Environment.SetEnvironmentVariable(
    "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT",
    observability.EnableSensitiveData ? "true" : "false");

var resourceBuilder = ResourceBuilder.CreateDefault().AddService(observability.ServiceName);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddSource(observability.AgentSourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddMeter(observability.AgentSourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
    logging.AddOtlpExporter();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
