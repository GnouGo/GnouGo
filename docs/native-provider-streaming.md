# Native provider streaming

`LLMStreamingMode` selects `Auto`, `Streaming`, or `NonStreaming` without changing the provider, model, endpoint or foreground/background protocol. The existing `CallAsync` returns one complete response. Intermediate provider events are recovery and inspection evidence; they never become planner contributions or business-tool invocations.

## Configuration

Provider defaults live in `RequestPolicy`; exact model/deployment overrides live in `ModelTransportOverrides`:

```json
{
  "LLM": {
    "Models": {
      "OpenAi": {
        "RequestPolicy": {
          "StreamingMode": "Auto",
          "StreamingSupport": { "ChatCompletions": true }
        },
        "ModelTransportOverrides": {
          "exact-deployment-id": { "StreamingMode": "Streaming", "StreamingSupport": { "ChatCompletions": true } }
        }
      }
    }
  }
}
```

The configured route's native protocols are `ChatCompletions`, `Responses`, `Messages`, `MessageBatches`, `OllamaChat`, and `Local`. A support declaration cannot enable a protocol the adapter does not implement: Message Batches is not a generation stream. Exact overrides are case-sensitive deployment ids, never fuzzy model matches. `stream_options.include_usage` is requested for chat unless the existing model metadata excludes `stream_options`.

The provider configuration wizard exposes transport mode and per-protocol route declarations. Its model review exposes an optional exact deployment mode and the existing nullable model streaming capability. Encrypted provider secrets use the existing flat payload convention (`streamingMode`, `streamingSupport`, `modelTransportOverrides`). Invalid present values fail configuration loading; they do not become Auto. DocIngestor Server/CLI chat scoring and vision settings accept the same policy as `OpenAi.GenerationTransport` / `Ollama.GenerationTransport`; their configured endpoint remains authoritative. Embeddings and asset downloads are not generation streams.

Resolution order is session capture, exact model override, provider default. An omitted provider mode means Auto for new calls. Auto streams only with confirmed native support on the selected route/model/protocol; unknown support selects non-streaming. Explicit Streaming rejects unknown or unsupported combinations before authentication/inference and never falls back to buffered generation. Custom gateways require route and exact model support, or an explicit protocol declaration on that exact deployment override. A provider route declaration alone cannot establish unknown model support. Official-route model declarations can establish support; a model explicitly excluding streaming remains unsupported.

New planning captures freeze requested/effective transport, protocol and capability evidence. Historical captures restore NonStreaming without changing stored sessions, requests, fingerprints, tariffs or reservations. Changing global transport settings affects new captures; existing route, tariff and retry-policy protection remains effective.

## Protocol and completion

| Adapter | Native transport | Completion proof |
| --- | --- | --- |
| OpenAI/Azure-compatible; configured Copilot-compatible | SSE, `stream:true` | Every received choice finished, then `[DONE]`; tool arguments assembled by choice/tool index |
| Existing OpenAI Responses route | Typed SSE, independently preserving `background:true` | Matching terminal `response.completed`, `.incomplete` or `.failed` object |
| Anthropic Messages | SSE, `stream:true` | Closed indexed content blocks, final stop reason and `message_stop`; cumulative usage merged by field |
| Ollama chat/vision | NDJSON, `stream:true` | Final `done:true`; content/thinking/tool calls accumulated, reported usage retained |
| LLamaSharp local | Native `InferAsync` iterator | Observed EOS/antiprompt or token limit, plus cancellation checked after enumeration |

Background Responses receipt recovery retrieves the same persisted response id from the same configured route. Retrievals are recorded under the original generation attempt and cannot create another inference or reservation. Cancellation remains terminal; captured retry/deadline limits remain effective. The planner retains its existing foreground-only issuance policy. Anthropic batch creation/polling stays separate and rejects explicit Streaming.

The common framer derives from the existing proxy reader: fragmented UTF-8, LF/CRLF, multiline SSE data and comment keepalives are supported, frames and response evidence are bounded. Malformed data is an error. Proxy adapters reuse that framer; no response is converted into artificial token chunks. The separate GitHub Copilot SDK tool integration already has native streaming/event controls and remains distinct from the AI provider and protocol proxy.

HTTP 200, connection closure, syntactically valid partial JSON, an initial usage zero, or a content-block stop do not establish completion. Refusals, output exhaustion, tool arguments, raw responses and usage reach the existing response mapping. Absent final usage stays unknown; conservative reservations and financial admission remain authoritative.

## Journals and inspection

The existing HTTP owner records the actual serialized request, transport selection, raw native response, assembled native response, terminal proof, response id, receipt retrievals, first/last byte timestamps and usage availability. Partial evidence is checkpointed in batches, with exact bytes retained encrypted when interruption splits UTF-8. Terminal evidence must persist before a response can return. Exact recovery uses that journal without dispatch or double settlement. Partial/uncertain generation does not authorize another POST or a supplier fallback.

The shared inspector displays effective transport and terminal facts, and pages native/assembled documents through its existing protected reader. Recovery-only base64 bytes never enter the inspection projection. Logical content is redacted before publication. Existing OpenTelemetry activities carry transport and completion tags; observability never authorizes recovery, admission or execution.

## Official protocol references and limits

- [OpenAI Chat streaming events](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events), [Responses events](https://developers.openai.com/api/reference/resources/responses/streaming-events), and [background receipt retrieval](https://developers.openai.com/api/docs/guides/background).
- [Anthropic Messages streaming](https://platform.claude.com/docs/en/build-with-claude/streaming).
- [Ollama streaming](https://docs.ollama.com/api/streaming), [chat](https://docs.ollama.com/api/chat), and [tool calling](https://docs.ollama.com/capabilities/tool-calling).
- [Pinned LLamaSharp 0.27 native iterator](https://github.com/SciSharp/LLamaSharp/blob/v0.27.0/LLama/LLamaStatelessExecutor.cs).
- [GitHub Models retirement](https://docs.github.com/en/github-models): protocol compatibility does not prove availability of the retired default service. No endpoint migration is automatic.

Streaming does not guarantee early bytes while a model reasons, gateway flushing, or avoidance of an external absolute cutoff. Live evidence must separate availability, complete generation, accepted planning work, execution and the independent business oracle. A short successful response does not prove that the earlier 300-second failure has been removed.
