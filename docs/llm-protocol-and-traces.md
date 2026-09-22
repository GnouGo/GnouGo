# Generation protocol and LLM traces

Use `/llm add` or `/llm edit openai` in Server chat. The connection card offers
**Background — Responses API** and **Chat Completions — foreground**. New OpenAI
configurations default to background Responses; existing `Auto` has the same meaning.
Editing preselects the effective setting. Review the summary and choose **save**;
**discard** leaves configuration unchanged. `/llm list` includes the protocol.

The selection is encrypted in the provider's KeyVault configuration as
`backgroundProtocol`. An absent field inherits the existing host configuration;
invalid or ambiguous values are rejected. Saving preserves other configuration,
including retry policy, credentials and API version. The running host uses the
selection for newly dispatched calls. The existing post-save validation call uses
the selected protocol. There is no automatic fallback, new retry layer or change
to planner reservations, receipts, budgets, approval or schema-7 storage.

## Inspect a call

Open **Traces** from a chat workflow or Workflow designer session, then select
**Pipeline / LLM calls**. The existing span timeline and Logs tabs remain available.
Calls are grouped by runtime stage: preparation, interpretation, choices, repairs
and execution. Other recorded stages and external tool boundaries are shown
separately. HTTP attempts belong to their logical call and do not increase its
call count. Replayed journal activity is not a newly dispatched inference call.

Choose **Inspect request / response** to load encrypted content. Request details
include the logical prompt, response schema and tool definitions; response details
include returned text/JSON, usage and model-issued tool calls. Repair requests
retain their issued targets, bindings, contracts and exact diagnostics. Use
formatted/raw rendering, content search and explicit copy buttons when comparing
requests. Closing or switching the panel cancels outstanding content loads.

The prompt analysis table reports exact UTF-8 byte sizes of logical sections and
**estimated** tokens (bytes divided by four, rounded up). These are not wire-body
sizes or provider token counts. Request-document size includes JSON framing;
prompt-section sizes refer to the decoded prompt and schema. Redacted content may
differ from the original request. Reported provider usage remains separate.

Missing usage or pricing is **unknown**. Totals show known usage and coverage;
possible usage from uncertain transport attempts is not assumed to be zero. Costs
are estimates with their currency. This diagnostic view does not replace durable
accounting. External MCP integrations expose only the telemetry they supply;
internal model content and usage are explicitly unavailable otherwise.

## Content storage and retention

`TraceDebug.Enabled=true` enables detailed capture. The shared repository
`appsettings.json` enables it for Server and Desktop. Set `--TraceDebug:Enabled=false`
to disable retained-content capture and reads. `TraceDebug.ContentRetentionDays`
defaults to **7**, and `TraceDebug.MaxDocumentBytes` defaults to **2097152** per
input/output document. Oversized documents are labelled, never silently truncated.
Disabling capture prevents new content storage and content reads; metadata spans
can still be retained by ordinary telemetry configuration.

New documents use the tenant-scoped, encrypted KeyVault record collection
`agent-llm-diagnostic-content-v1`. OpenTelemetry carries metadata and opaque
`gnougo.llm.content_ref` references, following the
[GenAI external-content pattern](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-spans.md#uploading-content-to-external-storage).
Full prompts and completions are not copied into ordinary Server step logs,
exported span attributes or workflow trace files. Provider configuration and
headers are never serialized. Known configured credential values are redacted;
raw provider responses, including private reasoning internals, are excluded.

Content lookup verifies tenant, original trace/span and, when supplied, planning
session ownership. Capture errors are observational: they cannot replace inference
results, initiate retries or change charges. A periodic worker removes expired
records from this diagnostic collection only.

Planner calls reuse encrypted reservations and receipts. Original schemas are not
rewritten. Historical calls without reliable span links appear under **Planning
journal history**, never as fabricated spans. Historical planner journals retain
their existing lifecycle and are excluded from diagnostic cleanup. A reservation
without a receipt is shown as uncertain and is never resent by the viewer.

## Validation

Run the Server, AI, Flow, Planning, integration and telemetry test projects in
Release. The Server tests cover protocol selection and HTTP routing, preservation
of settings, encrypted capture, failed/cancelled calls, parallel stage isolation,
ownership checks, retention, limits, journal reuse and safe lazy UI expansion.
The planner Native AOT and published Server persistence smokes remain unchanged.
See [the validation report](llm-protocol-traces-validation-2026-09-21.md) for this
change's offline and browser results.
