# Native streaming qualification — 2026-11-07

Native streaming is implemented across the AI providers while retaining the planner's complete-response contract. **The configured route supports real SSE, but streaming did not remove the long-call failure.** No new Amazon qualification was launched after that counterexample. The representative fragment produced neither an accepted contribution nor a complete receipt. Earlier Amazon plans, approvals, execution records and oracle failures remain unchanged.

## Implementation and activation

See [configuration and protocol documentation](../../native-provider-streaming.md). Provider defaults and exact model/deployment overrides expose `Auto`, `Streaming` and `NonStreaming` in the existing wizard and model editor. New sessions freeze their effective transport; historical captures remain non-streaming. Explicit Streaming requires confirmed native route/model/protocol support and cannot fall back to non-streaming or another supplier.

Covered implementations are OpenAI/Azure-compatible chat, configured Copilot-compatible chat, the existing OpenAI Responses route, Anthropic Messages, Ollama chat and embedded LLamaSharp. DocIngestor chat/vision adapters and the existing protocol proxy share the native framing path. Background Responses retrieval and Anthropic batches remain separate protocol choices. The retired GitHub Models default service was not live-qualified, and no configured endpoint was replaced.

Partial bytes, terminal proof, final usage and retrieval identities use the existing owned journals, common inspector and OpenTelemetry. HTTP 200 or EOF cannot complete a generation. Missing usage remains unknown. Completed historical Copilot/Ollama receipts can recover against their exact older payload without dispatch or fingerprint migration; new requests enforce native output bounds. Terminal refusals/failures retain accounting and cannot become planning contributions. Validation also found and fixed an existing typed-repair regression that cleared consumed evidence history while retaining prerequisites.

## Controlled comparison through Agent.Server

The same provider, exact deployment, prompt, schema, reasoning, output allocation, endpoint and retry/deadline policy were used for each pair. [Actual wire comparison](wire-comparison.json) verifies that only `stream` and `stream_options` differ. The [complete availability SSE (gzip)](stream-availability02-native.sse.gz) and [incomplete fragment prefix (gzip)](stream-fragment-native-prefix.sse.gz) are exact retained provider bytes; [hashes and terminal facts](native-wire-evidence.json) identify them. Each row is a separate request, not a replay. Each used one physical attempt, zero uncertain retries, a 600,000 ms complete-attempt deadline and no business tools.

| Experiment | Result | Duration | Completion/usage |
| --- | --- | ---: | --- |
| Non-streaming availability | Valid structured nonce response | 2.703415 s | Complete receipt; 104 input / 35 output tokens |
| Streaming availability, first attempt | Our decoder rejected Azure's empty-ID filter prelude | 2.597338 s | Partial evidence; full hold retained |
| Streaming availability, corrected decoder | Valid structured nonce response | 2.522754 s | Native SSE terminal proof and durable receipt; 104 / 35 tokens |
| Non-streaming representative fragment | Transport loss | 301.249038 s | No complete response or final usage |
| Streaming representative fragment | Native SSE prelude and assistant role, then transport loss | 307.263142 s | No terminal event, visible content, complete response or final usage |

The difficult fragment is the exact retained request-17 prompt/schema from session `0eb17725cede426bbeddde86cc51081b`, independently issued with `reasoning_effort: medium` and `max_completion_tokens: 32768`. Source `1404e0d8` was used for the corrected streaming calls; the non-streaming baseline used `59f69a2a`. The later output-bound/refusal/history fixes do not change these OpenAI request bodies. Result records contain source and loaded-assembly hashes.

The first streaming availability attempt revealed a real parser defect: Azure emits a metadata prelude with an empty ID before the actual generation ID. Commit `1404e0d8` ignores absent metadata identities, preserves filter annotations and late filtering, and tests both filtered and unfiltered completion. Its interrupted attempt remains inconclusive rather than a success or zero-cost call. The subsequent independently identified availability request succeeded.

For the representative streaming request, the server received 745 native bytes at `16:26:09.452339Z`, after dispatch at `16:26:01.775267Z`. The connection ended at `16:31:09.038409Z`, **299.586070 seconds after the last received bytes**. The journal retained HTTP 200 and the provider response identity, but no finished choice, `[DONE]`, assembled completion or final usage. This proves native streaming was enabled and consumed progressively. It does not establish why the upstream generation emitted no further bytes or which component ended the connection.

## Failure boundary and operator action

The approximately 300-second loss remains on the configured route despite an unchanged 600-second application deadline. The timing is consistent with an idle/read bound somewhere along that route; **the responsible hop, product and setting are not established**. There is no evidence authorizing blame on Amazon, the model or a particular proxy. The five-minute pooled-connection lifetime is not an active-request deadline: [.NET documents that active requests are not torn down by that setting](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.pooledconnectionlifetime?view=net-10.0).

The route operator needs to correlate the exact client attempt and provider identity in [wire-comparison.json](wire-comparison.json), then locate the disconnect at `16:31:09Z`. The required behavior is: permit an idle reasoning interval for the captured 600-second generation allowance, or forward native SSE keepalives during that interval; keep SSE unbuffered throughout the route. Any route/upstream read or idle bound responsible for the observed cutoff must cover that allowance. The actual product-specific directive cannot be named without the operator's configuration/logs. No inaccessible configuration has been changed or claimed fixed. Changing client tokens, retry count or timeout would not demonstrate this correction.

The persisted response is foreground Chat Completions. Its response ID is evidence, not proof of an authorized retrieval endpoint. There is no completed owned receipt and no captured background/retrieval contract for these calls. No Responses POST, alternate endpoint or blind generation replay was attempted. Native background retrieval remains tested independently using the existing Responses protocol.

## Retention and authorization

The original €400 spending authorization remains bound to its original configuration. The [immutable transport amendment](transport-amendment.json) chains through the existing capability amendment, changes only the selected deployment's streaming policy/declaration, and preserves prices, limits, endpoint, retries, consumption and every reservation. It grants no additional funding.

All three incomplete requests were checked with the existing eligibility mechanism and the user's standing authorization for future eligible tool-free generation retention. They are permanently inconclusive, non-replayable and non-continuable. No plan, approved artifact, executed artifact or business effect was produced by these probes.

| Incident | Entire retained reservation | Closure |
| --- | ---: | --- |
| Non-streaming fragment | €7.8622839229735781459919390952 | [Preview](nonstream-fragment-retention-preview.json), [retention](nonstream-fragment-retention.json) |
| Streaming availability parser failure | €2.7445768025078369905956112852 | [Preview](stream-availability-retention-preview.json), [retention](stream-availability-retention.json) |
| Streaming fragment | €7.8622839229735781459919390952 | [Preview](stream-fragment-retention-preview.json), [retention](stream-fragment-retention.json) |

[Record-integrity evidence](retention-record-integrity.json) confirms original record values remain unchanged. Availability successes are settled once; incomplete calls retain unknown usage and their whole holds. A retention is neither an inference success nor a reservation release.

## Budget and Amazon boundary

The final [public API audit](audit-final.json) records 265 physical attempts, 11 unresolved attempts, **€95.33317510076130765785938232** in measured-usage tariff estimates and **€75.218047469771607702642185404** in HTTP reservations. The original request-9 external hold remains **€2.4215763546798029556650246305**. The exact remaining allocation is **€227.0272010747872816838334076455**; see [exact balance](available-balance.json). Provider invoices and globally available funding remain unknown.

This qualification made five real inference requests: two completed availability calls and three inconclusive calls. It did not achieve representative generation, a new accepted contribution, a new accepted plan, delegated approval, workflow execution or an independent Amazon oracle pass. Identical paid repetition stopped after the streaming counterexample. Amazon remains blocked at representative generation, with its original requirements and oracle unchanged.

## Validation

Validation details and final run counts are recorded in [validation.json](validation.json). Native protocol fixtures cover fragmented UTF-8/framing, structured output, supported tool arguments, final/missing usage, refusals, output exhaustion, malformed events, mid-stream errors, interruption, cancellation, single-owner retries and exact recovery. Controlled accelerated gateway timing verifies progressive bytes and actual `stream:true`; `ResponseHeadersRead` alone fails that regression. A further regression verifies that a shorter explicit HTTP timeout still bounds the whole body after headers, in both modes. The live comparison's captured 600-second deadlines remain unchanged. Encrypted host recovery covers partial/terminal evidence and settlement without redispatch or double counting. Wizard/configuration persistence, immutable session precedence, historical capture and inspector redaction/rendering are covered by existing host tests.

Current-source validation passed all 32 non-Server assemblies (6,412 passed, 12 existing skips, zero failures), including 353 AI.Core, all 2,783 planning and all 97 DocIngestor tests. Another 279 targeted Server tests passed, including provider wizard, capture, inspector and financial/recovery boundaries. [Published checks](published-validation.json) record successful proxy and Planning Native AOT publishes/execution, the CI-supported trimmed Agent.Server publish, two separate encrypted-storage processes and all five affected Release packages. The frontend production build passed. The complete solution on `ac5b18e5` passed all 33 assemblies: **8186 passed, 13 existing skips, zero failures/compiler diagnostics, exit 0**, with unchanged tracked source. [Exact full results](full-solution-validation.json) retain that source identity rather than relabeling it as testing the later AI-layer fixes; [intermediate full validation](intermediate-full-validation.json) retains the earlier 8,146-pass/one-failure result and the exact typed-repair regression corrected afterward.

Live checks for Anthropic, Ollama, embedded GGUF inference and the retired default GitHub Models service were not performed. Their adapter tests are synthetic protocol/runtime tests; published proxy smoke checks are also controlled fixtures. No compatibility test is presented as proof of those services' live availability.
