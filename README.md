# OpenAgentOrchestrator

A standalone, self-contained .NET 10 service for hosting and executing multi-agent AI
orchestrator workflows. 

## Highlights

- **No external database.** Sessions live in memory; durable workflow checkpoints (both the
  step-level manifest and the Microsoft Agent Framework's own graph-level state) are stored as
  JSON files on local disk.
- **Config-file driven.** Providers, orchestrators, agents, and tools are all defined in one
  `config.yaml` — no CRUD endpoints, no schema migrations. Edit the file and call
  `POST /command/api/v1/config/$reload` to pick up changes without restarting.
- **Secrets live directly in `config.yaml`** (API keys, client secrets, header values) rather
  than as references into an external secret store. `config.yaml` is `.gitignore`d — only the
  placeholder `config.sample.yaml` is committed. Query endpoints redact secret fields before
  returning configuration.
- **[Agent Harness](https://learn.microsoft.com/agent-framework/concepts/harness)** support —
  any agent can be configured as `agentType: harness` to get the Microsoft Agent Framework's
  managed reasoning/tool-use loop, with configurable `HarnessInstructions`,
  `MaxContextWindowTokens`, `MaxOutputTokens`, and `DisableWebSearch` (to opt out of the harness's
  automatic hosted web-search tool - see [Configuring Agent Harness](#configuring-agent-harness)).
- **[Shell Tools](https://learn.microsoft.com/agent-framework/integrations/by-component/tools/shell-tools)**
  support — any agent (harness or plain chat) can optionally be given a local shell tool
  (`LocalShellExecutor`), configurable per-agent: `stateless` vs `persistent` mode, an explicit
  `acknowledgeUnsafe` opt-in, and a configurable `requireApproval` flag for command execution.
- **Custom, multi-provider web search tool** — any agent (harness or plain chat) can optionally
  be given a real `web_search` tool backed by Tavily, Bing, Google Custom Search, or SerpApi
  (selected via `webSearchTool.provider`), independent of the harness's own hosted web search
  (see [Configuring the Web Search Tool](#configuring-the-web-search-tool)).
- **Sequential workflow pattern** with optional durable checkpointing and human-in-the-loop
  review gates between steps, with resume/redo support (see [Resuming a checkpointed
  session](#resuming-a-checkpointed-session)).

## Solution Structure

- **Service** — plain ASP.NET Core 10 entry point (`WebApplication.CreateBuilder`), Swagger,
  health check, and the Command/Query controllers. Owns `config.yaml` / `config.sample.yaml` and
  the `instructions/` folder (agent prompt/response-schema files — see [Instructions & Response
  Schema Files](#instructions--response-schema-files)).
- **Command.Application** — the sequential workflow engine, checkpointing, in-memory session
  store, agent factory (chat agents + harness agents), shell tool factory, web-search tool
  factory (multi-provider: Tavily/Bing/Google/SerpApi), chat-client factory, MCP tool binding,
  and the YAML `ConfigStore`/`ConfigValidator`. Registered via a plain
  `IServiceCollection.AddCommandApplication()` extension method.
- **Command.Contract** — `ExecuteRequest`/`ExecuteResponse`/`ResumeRequest`/`ValidationResult` DTOs.
- **Command.Domain.Model** — `PlatformConfig` and the orchestrator/agent/provider/tool
  configuration model types (parsed from `config.yaml`), including `AgentType`, `HarnessOptions`,
  `ShellToolDefinition`, and `WebSearchToolDefinition`.
- **Query.Application** — thin read-side service projecting config/session/checkpoint state into
  `Query.Domain.Model` response DTOs, registered via `AddQueryApplication()`.
- **Query.Domain.Model** — `OrchestratorSummaryResponse`, `SessionStatusResponse`,
  `SessionCheckpointsResponse` read DTOs.
- **Application.UnitTests** — MSTest unit tests for the engine/config/agent/shell-tool components.

## Getting Started

1. Copy the sample config and fill in real values:
   ```powershell
   Copy-Item Service\config.sample.yaml Service\config.yaml
   ```
   Edit `Service\config.yaml` and set a real provider `apiKey`/`endpoint` (and any tool
   credentials you need). **Never commit this file** — it is already `.gitignore`d.
2. Restore and run:
   ```powershell
   dotnet restore
   dotnet run --project Service
   ```
3. Swagger UI is available at `/swagger` in Development. Health check at `/healthz`.

## Configuring Agent Harness

Set `agentType: harness` on any agent in `config.yaml` and add a `harness:` block:

```yaml
agents:
  - name: research-agent
    agentType: harness
    provider: azure-openai-main
    model: "gpt-4o"
    instructions: "You are a research assistant focused on academic sources."
    harness:
      harnessInstructions: "Use tools deliberately and report verified results."
      maxContextWindowTokens: 128000
      maxOutputTokens: 16384
      disableWebSearch: true   # see note below
```

This maps to `chatClient.AsHarnessAgent(new HarnessAgentOptions { ... })` in
`AgentFactory.CreateHarnessAgent`. Omit `agentType` (or set it to `chat`, the default) for a plain
`ChatClientAgent`.

**`disableWebSearch`** — by default (`false`, matching the framework's own default), the harness
automatically attaches a model-provider-hosted web search tool. Some `IChatClient`
providers/deployments reject its request parameters outright — for example, some Azure OpenAI
deployments respond with `HTTP 400 (invalid_request_error: unknown_parameter)` /
`Unknown parameter: 'web_search_options'`. Set `disableWebSearch: true` when:
- your provider doesn't support the hosted tool (as above), or
- you're attaching your own [web search tool](#configuring-the-web-search-tool) instead — per
  Microsoft's guidance, leaving this `false` while also adding a custom search tool gives the
  agent *two* search tools at once, which is redundant and can confuse the model.

## Configuring Shell Tools

Any agent (harness or chat) can be given a local shell tool by adding a `shellTool:` block:

```yaml
    shellTool:
      enabled: true            # attach a LocalShellExecutor to this agent
      mode: persistent          # "stateless" (fresh shell per call) or "persistent" (state carries across calls)
      acknowledgeUnsafe: true   # must be explicitly true - shell execution is inherently unsafe
      requireApproval: true     # require caller/human approval before each shell command executes
```

This wires up `LocalShellExecutor`/`ShellEnvironmentProvider` (`Microsoft.Agents.AI.Tools.Shell`)
and attaches `.AsAIFunction(requireApproval: ...)` to the agent's tool list, with the executor's
lifetime scoped to the agent. See `Command.Application/Tools/ShellToolFactory.cs`.

## Configuring the Web Search Tool

Any agent (harness or chat) can be given a real, working `web_search` tool by adding a
`webSearchTool:` block. Unlike the harness's own hosted web search (see
[`disableWebSearch`](#configuring-agent-harness)), this tool calls a genuine third-party search
REST API of your choosing and works with any `IChatClient` provider:

```yaml
    webSearchTool:
      enabled: true
      provider: tavily           # "tavily" (default) | "bing" | "google" | "serpapi"
      apiKey: "<REAL_PROVIDER_API_KEY>"
      maxResults: 5
      searchDepth: basic         # Tavily-only: "basic" | "advanced" | "fast" | "ultra-fast"
      # searchEngineId: "<CX_ID>"  # required when provider: google
      # searchEngine: google       # SerpApi-only: which engine SerpApi should proxy
```

Supported providers (selected via `provider`, case-insensitive):

| Provider  | Required fields                       | Notes                                             |
|-----------|----------------------------------------|----------------------------------------------------|
| `tavily`  | `apiKey`                               | Default. Purpose-built for LLM/agent search; simple REST API, free tier. Also supports `maxResults`, `searchDepth`. |
| `bing`    | `apiKey`                               | Bing Web Search API v7. Also supports `maxResults`. |
| `google`  | `apiKey`, `searchEngineId`             | Google Custom Search JSON API; `searchEngineId` is the Programmable Search Engine "cx" id. Also supports `maxResults` (max 10/request). |
| `serpapi` | `apiKey`                               | SerpApi; `searchEngine` selects the underlying engine it proxies to (default `google`). Also supports `maxResults`. |

This wires up an `AIFunction` named `web_search` (via `AIFunctionFactory.Create`) that calls the
selected provider's REST API through `IHttpClientFactory` and returns normalized
title/url/snippet results to the model. See
`Command.Application/Tools/WebSearch/WebSearchToolFactory.cs` and the per-provider
`IWebSearchProvider` implementations alongside it.

**Interaction with `harness.disableWebSearch`** (harness agents only): these two settings are
independent, so choose the combination that fits your scenario:

| `harness.disableWebSearch` | `webSearchTool.enabled` | Result                                              |
|----------------------------|--------------------------|------------------------------------------------------|
| `false` (default)          | `false` (default)        | Only the hosted tool (if the provider supports it). |
| `true`                     | `true`                    | **Recommended when adding a custom tool** — only the custom tool, avoiding the redundant/confusing "two search tools" case. |
| `false`                    | `true`                    | Both tools attached at once — redundant, not recommended. |
| `true`                     | `false`                   | No web search at all. |

## Instructions & Response Schema Files

Agent `instructions` and structured-output `responseFormat.schema` can be authored either inline
in `config.yaml`, or as standalone files under a configurable folder (default: `Service/instructions/`,
see `ConfigYaml.InstructionsRoot` in `appsettings.json`) — useful for long/complex prompts and
JSON schemas that are unwieldy as inline YAML strings:

```yaml
agents:
  - name: research-agent
    instructionsFile: "research-agent.instructions.md"   # relative to InstructionsRoot
    responseFormat:
      type: json_schema
      schemaFile: "research-summary.schema.json"          # relative to InstructionsRoot
```

- Files are resolved once, at config-load/reload time, into the in-memory
  `Instructions`/`Schema` values — nothing else in the pipeline needs to know the difference.
- If both the inline field (`instructions` / `responseFormat.schema`) and its file counterpart
  are set, the **inline value always wins** and a warning is logged; the file is not read.
- A missing referenced file fails config load/reload with a clear error (same failure path as a
  missing/invalid `config.yaml`).
- These files contain only prompts/schemas — never secrets — so, unlike `config.yaml`, they are
  safe to commit. See `Service/instructions/` for the sample files referenced by
  `config.sample.yaml`.

## REST API

| Method & Path | Description |
|---|---|
| `POST /command/api/v1/orchestrators/{orchestratorId}/$execute` | Starts (or continues) a session. Accepts JSON (`{ "input": "...", "sessionId": "...", "context": {...} }`) or `multipart/form-data` (text `input` or a `file`, plus `sessionId`/`context[...]` fields). |
| `POST /command/api/v1/sessions/{sessionId}/$resume` | Resumes a checkpointed session — see [Resuming a checkpointed session](#resuming-a-checkpointed-session) below. The orchestrator is resolved automatically from the session's durable checkpoint. |
| `DELETE /command/api/v1/sessions/{sessionId}/checkpoint` | Deletes a session's durable checkpoint once it has reached a terminal state (`completed`/`failed`/`rejected`); `409 Conflict` if still `running`/`pending_approval`. |
| `POST /command/api/v1/config/$reload` | Re-reads and re-validates `config.yaml` from disk; keeps the previous snapshot active if validation fails. |
| `GET /query/api/v1/orchestrators-config` / `/{orchestratorId}` | Full orchestrator definitions (agents, tools, harness/shell-tool config) with secrets redacted. |
| `GET /query/api/v1/orchestrators/{orchestratorId}` | Orchestrator summary. |
| `GET /query/api/v1/providers` | Provider definitions with secrets redacted. |
| `GET /query/api/v1/sessions/{sessionId}` | Session status. |
| `GET /query/api/v1/sessions/{sessionId}/checkpoints` | Durable step checkpoints (requires checkpointing to be enabled for the orchestrator). |

`orchestratorId` is the `id` of an entry under `orchestrators:` in `config.yaml`.

### Resuming a checkpointed session

`ResumeRequest` has three fields:

- **`action`** (required) — `continue`, `reject`, or `redoFromStep`.
- **`stepIndex`** (required only for `redoFromStep`) — the 0-based index (from
  `GET .../checkpoints`'s `Steps[]`) of the step to redo.
- **`editedOutput`** (optional) — for `continue`, an edited replacement for the pending step's
  output; for `redoFromStep` with `stepIndex: 0`, an override for the session's original input.

| `action` | Effect |
|---|---|
| `continue` | Approves the paused review request (`humanInLoop`), optionally with `editedOutput`. Requires status `pending_approval`. |
| `reject` | Abandons the session (terminal — `rejected`). Requires status `pending_approval`. |
| `redoFromStep` | Re-executes the named step and everything after it, discarding prior outputs; with `stepIndex` equal to the current step count, this is also how a crashed/interrupted run is resumed. |

Checkpointing alone (without `humanInLoop`) already gives crash/failure recovery: if a step
throws, or the process is killed mid-run, `POST .../$resume` with
`{ "action": "redoFromStep", "stepIndex": <Steps.Count> }` picks up from the last successfully
completed step.

## Security Notes

- `Service/config.yaml` holds real secrets and is `.gitignore`d — only `config.sample.yaml`
  (placeholders only) is committed. **Never commit `config.yaml`.**
- Query endpoints that return provider/orchestrator/tool definitions redact `apiKey`,
  `clientSecret`, and header values via `ConfigRedaction` before serializing the response.
- Shell tools are disabled by default (`shellTool.enabled: false`) and require an explicit
  `acknowledgeUnsafe: true` to turn on, since local shell execution is inherently unsafe.
- Web-search tool API keys (`webSearchTool.apiKey`) are stored directly in `config.yaml`, like
  other secrets in this project, and are redacted by `ConfigRedaction` before being returned over
  query endpoints.
- There is no authentication/authorization wired up by default (this is a hackathon fork with
  Spine platform auth removed) — do not expose this service on a public network as-is.

## Docker

A minimal multi-stage `Service/Dockerfile` is provided. Mount `config.yaml` as a volume/secret at
runtime rather than baking it into the image:

```powershell
docker build -t open-agent-orchestrator -f Service/Dockerfile .
docker run -p 8080:8080 -v ${PWD}/Service/config.yaml:/app/config.yaml open-agent-orchestrator
```

## Observability

The orchestrator is instrumented end-to-end with OpenTelemetry (traces, metrics, logs) and ships
with a local Docker Compose stack for viewing them.

**Instrumentation:**

- **Plain chat agents** (`ChatClientAgent`) are instrumented at the agent boundary only, via
  `AIAgent.AsBuilder().UseOpenTelemetry(sourceName, configure)` in `AgentFactory.CreateChatAgent`.
- **Agent Harness agents** are instrumented via `HarnessAgentOptions.OpenTelemetrySourceName` in
  `AgentFactory.CreateHarnessAgent`, which — per the Microsoft Agent Framework's design —
  automatically instruments *both* the harness's internal chat client and its agent boundary from
  a single source name (no separate call needed, avoiding duplicate spans).
- The `ActivitySource`/`Meter` name (`Observability:AgentSourceName`, default
  `OpenAgentOrchestrator.Agents`) and service name (`Observability:ServiceName`) are configurable
  in `appsettings.json`. Whether prompts/responses are captured on spans is controlled by
  `Observability:EnableSensitiveData` (`false` by default, `true` in `appsettings.Development.json`)
  — this maps to the OTel GenAI convention env var
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`, set once at startup in `Program.cs`.
- ASP.NET Core, HttpClient (traces + metrics) and .NET runtime (metrics) instrumentation are also
  registered in `Program.cs`, alongside the OTLP exporter for traces/metrics/logs.
- The OTLP **endpoint is never hardcoded** — `AddOtlpExporter()` reads the standard
  `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` environment variables natively;
  these are only set in `docker-compose.yml`. If the Collector is unreachable, the OTLP exporter's
  batch processor retries/drops in the background — it never throws into request-handling code,
  so the app keeps working with the Collector down.

**Target architecture:**

```text
.NET Agent Orchestrator
        │
        │ OTLP
        ▼
OpenTelemetry Collector
     │    │    │
     │    │    └── Logs   ──▶ debug exporter (collector stdout)
     │    └────── Metrics ──▶ debug exporter (collector stdout)
     └─────────── Traces  ──▶ Jaeger
```

**Files:**

| File | Purpose |
|---|---|
| `Service/Program.cs` | OpenTelemetry SDK wiring (tracing/metrics/logging providers, OTLP exporter, ASP.NET Core/HttpClient/Runtime instrumentation). |
| `Command.Application/Configuration/ObservabilityOptions.cs` | `Observability` config section (`ServiceName`, `AgentSourceName`, `EnableSensitiveData`). |
| `Command.Application/Agents/AgentFactory.cs` | Agent-level (`UseOpenTelemetry`) and Harness-level (`OpenTelemetrySourceName`) instrumentation. |
| `Service/appsettings.json` / `appsettings.Development.json` | `Observability` section + dev override for sensitive-data capture. |
| `observability/otel-collector-config.yaml` | Collector receivers/processors/exporters/pipelines. |
| `docker-compose.yml` | `orchestrator` + `otel-collector` + `jaeger` services. |

**Run it:**

```powershell
Copy-Item Service\config.sample.yaml Service\config.yaml   # if you haven't already
docker compose up --build
```

**Verify:**

1. Send a request, e.g. `POST http://localhost:8080/command/api/v1/orchestrators/{id}/$execute`.
2. **Traces** — open the Jaeger UI at <http://localhost:16686>, select the `OpenAgentOrchestrator`
   service, and confirm spans for the HTTP request and the agent/chat-client calls.
3. **Metrics** and **logs** — run `docker compose logs -f otel-collector` and confirm `debug`
   exporter output for both (metrics appear as periodic batches; logs appear per request).
4. **Resiliency** — run `docker compose stop otel-collector`, then repeat step 1: the request
   should still succeed (telemetry export failures don't affect business logic).

