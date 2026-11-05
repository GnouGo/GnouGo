# Diagnostic planning and local LLM inspection

Start the local server normally:

```sh
dotnet run --project src/GnOuGo.Agent.Server
```

The normal `GnOuGo.Agent.Server` launch profile opens the protected operator window
automatically. **Diagnostic — thresholds are warnings** is the default for new
authenticated local plans, in both Designer and chat. Strict remains an explicit
choice. The `Diagnostic` launch profile is a compatible alias. Use the automatically
opened window: a separately opened browser may not have its operator cookie.
Ordinary planning without that cookie remains available in Strict mode.

Local startup opens a loopback browser, exchanges a five-minute single-use fragment
token for an eight-hour HttpOnly, SameSite=Strict protected cookie, and removes the
token from browser history. Tokens never enter HTTP URLs or application logs.
Restart the server for a fresh bootstrap window. Remote addresses, non-loopback
hosts and foreign origins cannot activate or read diagnostics. Provider settings,
captured Limited ceilings, workspace rules, approvals and replay restrictions remain effective.

Threshold policy is independent of Interactive/Auto and Ordinary/Large. The chat bridge
passes the authenticated grant outside model-authored workflow inputs. Standalone
factories reject reserved host options. Existing sessions acquire no new authority.

`TypedWorkflowPlanning:DiagnosticEnabled` and `OpenLocalOperatorBrowser` default to
true. `DefaultThresholdMode` defaults to `diagnostic` for authenticated local
operators; set it to `strict` for an explicit host opt-out. Omitted API selections
use this authorized default; explicit unauthorized diagnostic requests are rejected.
Headless hosts can set `OpenLocalOperatorBrowser=false`. `DiagnosticModelMaximum`
defaults to false: navigation, drafting/correction and review use configured phase
allocations, including phase-policy overrides. Set it explicitly to true to request
the verified model output maximum, restricted by an explicit deployment cap.
The Diagnostic profile does not override this setting. Warning authority and output
allocation are independent. There is no artificial 65,536 output ceiling.
In Limited mode, financial admission can refuse the complete reservation, never silently reduce it.
Requested/effective allocations and restriction origins remain inspectable.

The OpenTelemetry trace panel opens on **Pipeline / LLM calls**. Capture is enabled
by default; full redacted inputs/outputs load on demand from encrypted storage.
Exported spans carry measurements and content references, not business prompt or
response bodies. Existing collector endpoints and explicit export opt-outs remain
unchanged.

## Application LLM consumption

Desktop and standalone Server default to **Unlimited — usage and costs tracked**,
independently of environment. Open **Application LLM budget** in the Designer
(`/settings/llm-budget`) to save Limited or Unlimited. A saved, encrypted local
operator preference overrides `ApplicationLlmBudget:Mode`; its values are
`unlimited` (default) and `limited`. Changes apply only to new plans and executions.
The setting uses the same protected local operator session; HTTP edits also require
antiforgery. It is not a workflow/model-authored option.

Limited uses `WorkflowPlanningBudget` (the configured €50 default) and the existing
planning targets, and `Flow:Execution:LlmBudget` for executions. Diagnostic planning
targets retain their advisory behavior; monetary ceilings remain mandatory in Limited.
Unlimited uses an explicit tracking-only scope with no application monetary,
cumulative call/token or total LLM-time ceiling. Ordinary finite contracts still
require positive configured limits. Explicit workflow-owned limits remain mandatory.
Provider quotas/charges, physical capacity, finite request timeouts, bounded retries,
cancellation, reviews, semantic validation, approvals and execution CPU/memory,
iteration/concurrency limits remain effective.

Accounting persists usage, exact captured tariff estimates, physical attempts and
uncertain reservations. Unavailable prices or exchange quotes are labelled unavailable;
they do not block Unlimited or mean zero cost. Existing sessions/runs keep their
original captures, charges and reservations on restart, including the retained incident.

Desktop hands the existing one-use fragment bootstrap into Photino and exchanges it
for the protected cookie there. Its opt-in browser fallback uses a new one-use link.
Authority links are never written to Desktop boot logs. Native window automation is
separate from the controlled HTTP/Blazor handoff check.

A stopped budget session can offer **Start new plan with current settings** after
checking the session lease, counters, encrypted request/receipt/HTTP journals and
reservations for absence of dispatch. This creates a new identity; the original
remains unchanged. Missing or uncertain dispatch evidence cannot authorize replay.
New budget failures retain scope, origin, allowance, required reservation, currency
and dispatch state; old failures without those fields explicitly show unavailable.

## Effective default model

The host resolves persisted provider selection and KeyVault configuration before
workers or the operator browser start. For the selected provider, the KeyVault model
wins over a persisted user model; absent a configured model, the persisted user model
then the host default apply. An unavailable persisted provider does not donate its
model identifier to another provider. Chat, new runtimes and the provider API use the
same resolver; later MCP endpoint publication cannot overwrite the result.
Startup logs the effective provider/model and source. `/api/llm/providers` exposes
`defaultModelSource`. Conflicting stale user defaults produce a redacted warning and
are not rewritten. Private deployment identifiers remain exact configuration.
Existing planning sessions keep their captured models, tariffs and reservations.

Before the first durable checkpoint, the host captures authority, threshold values,
phase policy, exact declared model/deployment metadata, tariffs, route identity and
finite transport policy. A separate encrypted tenant/session record authenticates
the capture. Restart verifies it and uses captured capabilities and prices. Changed
routes or request policies fail with a specific cause. Historical charges and
reservations remain unchanged. Missing historical evidence stays unavailable;
exact receipt recovery remains possible, but new dispatch cannot invent old
metadata from today's catalog.

## Advisory and mandatory limits

| Limit or invariant | Strict | Fresh authorized diagnostic session |
| --- | --- | --- |
| Application cumulative calls/tokens/LLM time | No ceiling in Unlimited; captured limits in Limited | No ceiling in Unlimited; Limited planning targets remain advisory |
| Corrections, active planning targets | Enforced | Durable warnings through downstream adapters |
| Estimated input, cumulative tokens, context and prompt/response targets | Enforced according to capture | Warnings; complete mandatory context retained |
| Fragments, checkpoints, blocks, operations, nodes, graph/frontier and recursive task counts | Enforced | Warnings; planner-owned schema maxima removed |
| Discovery/retrieval targets, nonproductive-call counts | Enforced | Warnings; exact source identities/pagination required |
| Completed repeated generation strategy | Stops with retained evidence | Warns under separately captured `diagnostic_progress_policy: advisory-v1`; retains rejection, receipts and repeat history |
| Invalid/empty output, cardinality, types, references, uniqueness, expression rules | Reject | Reject; invalid work never becomes accepted progress |
| Business judges, mandatory reviews, permissions, ownership, artifact approval, workspace confinement | Required | Required |
| Application financial ceiling | Mandatory in Limited; absent in Unlimited | Mandatory in Limited; absent in Unlimited |
| Future-review/unknown-usage accounting; explicit workflow limits | Retained / enforced | Retained / enforced; no uncertain replay or automatic release |
| Verified provider context/output/endpoint/reasoning capabilities | Required | Required; no protocol/model fallback |
| Per-request timeout, bounded HTTP retries, cancellation | Required | Required; session targets cannot replace request timeout |
| Uncertain dispatch, incomplete HTTP body, effects, invalid/cyclic discovery continuation | No blind replay | No blind replay |
| Execution iteration/concurrency/memory/statements/recursion budgets | Enforced | Enforced independently of planning |

Historical token-only and diagnostic optimization captures keep their original
scope and repeat behavior. Advisory targets never widen repair authority or alter
contracts. Output allocations require physical and applicable captured financial admission before
request hashing/reservation.

## Timeline and exchanges

The page refreshes every two seconds. Durable engine events identify understanding,
business selection, implementation selection, discovery, fragment generation,
technical validation, local/global review, correction, compilation and readiness
for approval. Entry is saved before long calls. Occurrences retain UTC start and
duration, request, business responsibility, fragment, checkpoint, outcome and reason.
Skipped phases, loops, warnings, blocking and cancellation survive reload/restart.
Execution remains separate. No percentage or extra summarization call is used.

Overall progress reuses the deterministic TaskPlan projection: expected results,
accepted contributions, connections, remaining obligations and composition findings.
There is no second plan or persistent progress cache. Events and call journals
refresh independently of session revision.

Expand an exchange for the actual serialized HTTP request and every attempt's
observed status, timing, response, failure, retry evidence and completeness.
Partial bodies remain inspection evidence, never replayable completions. Effective
reasoning/output/retry settings, raw usage/cache/reasoning details, validation and
linked warnings accompany calls. Reasoning tokens already belong to output totals.
Cost labels distinguish measured usage, estimates, usage-times-captured-tariff
estimates, uncertain reservations and unavailable provider invoice costs.

Planning retains engine progress, phase/block, warnings and blocking causes.
Timeline request links open the same `TraceSidebar`, `TracePipeline`, `LlmCallDetail`
and `LlmTraceDocument` used by chat. Exact tenant/session-owner/request identities
join spans and journals into one logical call, with nested HTTP attempts. Calls
span all owned traces; the trace selector controls raw spans/logs. Missing telemetry
is labelled as journal evidence, with no fabricated spans, durations or acceptance.
Receipt, technical validation and business review remain separate facts.

Content loads when expanded and reads encrypted 32,768-character pages on demand,
with bounded server-side search continuations and streaming complete redacted JSON
export. The viewer retains its current page, not a hydrated full document.
Credentials, tokens, cookies, sensitive
headers and endpoint secrets are removed, including decoded JSON string content.
Large documents are neither withheld nor silently truncated. Inspection grants
no planning authority and dispatches no inference.

Protected reads:

- `GET /api/planning/{id}/inspection?after={sequence}&callsAfter={encodedCursorJson}`
  returns up to 200 events and a bounded metadata-only call delta. Pass
  `nextCallCursor` back as URL-encoded JSON; follow `hasMoreCalls`.
- `GET /api/planning/{id}/calls/content?key={exactKey}` returns request-only
  accounting, effective settings, decisions and document references, without bodies
  or the complete session budget.
- Add `document={id}&version={version}&offset=0&count=32768` to read text, total
  characters, exact next offset and completeness. Add `search={query}` for bounded
  search with `nextMatch` / `nextOffset` continuation. Changed versions return 409;
  never concatenate pages from different versions.
  The maximum 65,536 characters is a display page size.
- `GET /api/planning/{id}/calls/export?key={exactKey}` returns the complete redacted
  exchange. Use `workflow=true` only for the original chat-owned journal.
- `GET /api/traces/llm/export?reference={id}&trace={trace}&span={span}&session={session}`
  exports the same documents for a trace-owned nonplanning call.
- API mutations require the request token/header returned by
  `GET /api/planning/antiforgery`. Blazor uses its protected circuit.

Every read verifies current tenant, exact session, original version-11 journal
owner and request key. Cross-session/request/document substitution fails. Existing
KeyVault storage supplies bounded metadata queries and authenticated chunks;
full-record replay reconstructs identical logical bytes. Optional observers prepare
redacted document parts after journal persistence and grant no planning/replay
authority. Interrupted preparation cannot publish a partial descriptor. No new
database service, synchronization worker or progress cache is introduced.
Explicit access to an older monolithic document may decrypt it once to prepare
pages. Original logical content, timestamps, charges and reservations remain
unchanged; subsequent reads use pages. Refreshes neither convert old documents nor
list/decrypt tenant history. Diagnostic retention includes parts; planner replay
journals retain their existing lifetime.

## OpenAI sources and billing

Official documentation was verified on **2026-11-05**; entries retain verification
dates and URLs. Regenerate with `bash scripts/update-model-metadata.sh` or the
existing PowerShell generator, without the third-party download option. Private
dated deployment names remain explicit configuration; no public snapshot is invented.

| Documented model | Context / maximum output | Standard input / cached input / output USD per million |
| --- | --- | --- |
| [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra) | 1,050,000 / 128,000 | 10 / 1 / 50 |
| [GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) | 1,050,000 / 128,000 | 2 / 0.10 / 10 |
| [GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra) | 1,050,000 / 128,000 | 1 / 0.05 / 5 |
| [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna) | 1,050,000 / 128,000 | 0.10 / 0.01 / 0.50 |
| [GPT-6 Sol](https://developers.openai.com/api/docs/models/gpt-6-sol) | 1,050,000 / 128,000 | 2 / 0.20 / 10 |

These models support streaming, text/image input, text output and structured output.
Astra/Sol 6.1 accept low/medium/high/xhigh/max reasoning; Terra/Luna/Sol 6 also accept
none. Tools require Responses for Astra/Sol 6.1/Terra; Chat tools for Luna/Sol 6
require none. Temperature is limited to none where supported. Public capabilities
are separate from configured gateway availability.

[Official pricing](https://developers.openai.com/api/docs/pricing) specifies cache
writes (30 minutes 1.25× input, 12 hours 2.5×), input above 272,000 tokens (2×
input/cache and 1.5× output for the entire request), fast 2×, flex/batch 0.5×,
documented ultrafast 6× (Astra/Sol 6.1), and applicable regional processing 1.1×.
Captured `RequestPolicy.BillingServiceTier`, `BillingRegionalProcessing` and
`BillingCacheRetention` (`30m` or `12h`) declare configured billing conditions; they do not enable gateway capabilities. Reported
cache/usage/service-tier data refines settlement; reservation stays conservative.
Reasoning is never added again to billed output. See official
[deprecations](https://developers.openai.com/api/docs/deprecations).

Source-bound checks, screenshots and the live-provider preflight barrier are in
[the evidence report](evidence/diagnostic-planning-2026-11-05/README.md).
