# LLM HTTP recovery validation

This change keeps the compact planner architecture, schema-7 sessions, approval and runtime confirmation boundaries unchanged. No paid model calls, campaign resumptions, real workflow integrations or GitHub publications were performed. Existing live campaign evidence and accounting were not opened or modified.

## Retry ownership

`GnOuGo.AI.Core/HttpRequestHelper.cs` owns the transport loop for all built-in HTTP providers. It retries only HTTP 425, 429, 500, 502, 503 and 504, plus eligible network/timeouts. Authentication, authorization, quota/billing, other statuses, permanent transport/configuration failures and caller cancellation do not retry. Error-envelope classification prevents quota-shaped 429 responses from being retried.

`Retry-After` is mandatory and accepts delta seconds or an HTTP date. A delay exceeding the total delay allowance stops recovery instead of being shortened. Otherwise the helper uses bounded full-jitter exponential backoff. The per-attempt deadline covers headers and the complete response body. Restart retains scheduled retry deadlines and cannot stretch a wait beyond the configured bound after a clock change.

Default policy:

| Setting | Default |
| --- | --- |
| Total HTTP attempts, including the first | 4 |
| Uncertain retries | 1 |
| Per-attempt timeout | 600,000 ms, additionally bounded by the HttpClient/caller timeout |
| Initial exponential-backoff bound | 1,000 ms |
| Maximum jitter backoff | 30,000 ms |
| Total delay allowance | 60,000 ms |

The provider `RetryPolicy` configures these limits. Agent option copies preserve every setting. An optional validated `retryPolicy` object in the consumer-owned KeyVault provider configuration replaces the base policy; unspecified fields use defaults. Unknown/ambiguous fields, invalid types and invalid limits fail validation. The live benchmark pins the effective retry policy for its campaign.

The OpenAI and Anthropic protocol fallback/cache paths were removed. Non-transient responses no longer cause another request through a different endpoint or with the token ceiling omitted. `RequestPolicy.BackgroundProtocol` selects the OpenAI protocol before dispatch. The obsolete cache constructor arguments and host wiring were removed with their historical behavior tests; replacement tests assert one dispatch and unchanged protocol/limits on rejection.

## Durable uncertain recovery

`LLMHttpRetryContext` supplies a host journal for one synchronous, side-effect-free generation operation. It is not another retry loop. The HTTP helper requires this accounting boundary before retrying an uncertain POST. The routing client rejects tool-bearing and background generation when this context is active; other external-effect operations are not opted in. GET operations can recover without model-usage reservations.

Each physical attempt gets a new `X-Client-Request-Id` and is saved before dispatch. The journal records complete responses, known failures and unknown reservations. A restart replays an available HTTP completion without another send. If completion is missing, the original identity is never resent: recovery consumes the remaining uncertain allowance and reserves a fresh identity. Caller cancellation and exhausted attempts remain terminal after restart. Receipt-store errors do not trigger an in-process transport retry.

The ordinary production planning journal remains single-attempt unless its host supplies this accounting integration. The built-in KeyVault live benchmark enables it. External command adapters retain their own responsibility for transport and accounting; the benchmark does not add an outer retry loop around them.

## Benchmark accounting and continuation

`BenchmarkHttpJournal.cs` handles admission and encrypted evidence through the public KeyVault record API. It contains no transport retry loop. Admission sums existing verified campaign usage, legacy budget snapshots and all outstanding conservative allowances before accepting a new attempt. The EUR 50 campaign ceiling and eight physical attempts per session remain enforced; repairs still mean intent corrections and retain the two-repair limit.

Unknown usage remains unknown after successful recovery. The full configured input/output allowance and its pricing/FX estimate remain reserved for each uncertain attempt. Actual usage from a later successful attempt is recorded separately. Total token/cost fields remain null when incomplete; known usage, reserved tokens/cost and their combined cost bound are separately inspectable. Campaign summaries expose cumulative `campaign_accounting`.

One deterministic recovery example records two calls: the first has unknown usage with a 100-input/20-output-token, EUR 1 allowance; the second reports 10 input tokens, 2 output tokens and EUR 0.10. Cumulative known cost is EUR 0.10 and the cost bound is EUR 1.10. Restart after failure to write the final model receipt replays the stored HTTP response with the same totals and no third dispatch. These are synthetic test values, not measured provider prices or invoices.

Prepared journals support restart before dispatch. Durable model receipts and original request schemas remain unchanged. Updated failure details retain earlier distinct failures; identical replays do not duplicate history. Failed run results remain in `previous_results` when their pending request resumes. Successful bounded recovery can continue the campaign. Exhaustion, unavailable accounting, invalid usage or insufficient budget stops it without resetting state. Older uncertain reservations without the required HTTP evidence remain stopped; no receipt or delivery history is invented.

## Validation

Implementation commits, each tested and pushed:

- `a733a96`: shared HTTP recovery and durable attempt hooks.
- `d6159ab`: benchmark admission, accounting and restart.
- `1afc7c9`: removal of non-transient protocol fallbacks and cache wiring.
- `7a8c409`: durable cancellation and terminal-failure handling.
- `5594d70`: retained failure history, cumulative reporting and bounded restart delays.
- `2307078`: host policy copies, validated KeyVault overrides and mandatory Retry-After.
- `d5d9db7`: response-body timeout and disposal regression.

Focused deterministic coverage includes status allowlists, quota, permanent transport failures, timeouts, complete-body deadlines, Retry-After forms and bounds, jitter, cancellation, new identities, before-send admission, restart before dispatch, response/receipt replay, failure-history retention, exhaustion, call/cost ceilings, unknown-versus-known usage, original schema preservation and rejection of tools/background effects. Fake HTTP handlers and isolated encrypted stores are used; frozen workflow prompts remain unchanged.

Validation completed with SDK 10.0.300 on macOS arm64. Production release artifacts use `2307078`; the final solution run includes the additional body-timeout regression at `d5d9db7`.

| Check | Result |
| --- | --- |
| Complete Release solution suite | 2,512 passed, zero failed, one opt-in live GitHub E2E test skipped; 29 projects |
| AI.Core tests | 220 passed |
| Campaign persistence/recovery tests | 12 passed |
| Targeted host configuration and campaign tests | 33 passed |
| Release solution and benchmark builds, warnings as errors | Passed, zero warnings |
| AI.Core and Flow.Integrations package creation | Passed, zero warnings |
| Eight frozen offline workflows | All reached FinalReview and passed all 31 independent execution variants |
| Native AOT planner publish and eight-case smoke | Passed, zero warnings in normal publish |
| Trimmed, self-contained, single-file Agent publish | Passed, zero warnings |
| Published Agent persistence smoke | Schema-7 persistence, encrypted review drafts, tenant isolation and uncertain-publication replay passed |

Agent publishing used a clean archive of the committed source and an isolated persistence workspace, avoiding shared IDE restore artifacts. Bundled tools and frontend rebuilding were skipped for that persistence check. Frontend sources were unchanged. No new warning suppression was introduced; existing documented publish-local framework/Jint exceptions remain in place.

The recorded provider failures, usage and prices in the new tests are synthetic. These results establish bounded transport behavior and durable accounting, not a new live-model reliability rate. The previous stopped campaign is still untouched.
