# Diagnostic planning and local LLM inspection

Start the development operator window:

```sh
dotnet run --project src/GnOuGo.Agent.Server --launch-profile Diagnostic
```

The profile opens a loopback browser, exchanges a five-minute single-use fragment
token for an eight-hour HttpOnly, SameSite=Strict protected cookie, and removes the
token from browser history. Tokens never enter HTTP URLs or application logs.
Restart the profile for a fresh bootstrap window. Remote addresses, non-loopback
hosts and foreign origins cannot activate or read diagnostics. Provider settings,
money ceilings, workspace rules, approvals and replay restrictions remain effective.

Choose **Diagnostic — thresholds are warnings** before creating a plan. Strict is
the default, independently of Interactive/Auto and Ordinary/Large. The chat bridge
passes the authenticated grant outside model-authored workflow inputs. Standalone
factories reject reserved host options. Existing sessions acquire no new authority.

`TypedWorkflowPlanning:DiagnosticEnabled` enables the option;
`OpenLocalOperatorBrowser` opens the bootstrap window. `DiagnosticModelMaximum`
defaults to true: navigation, drafting/correction and review each request the
verified model output maximum, restricted by an explicit deployment cap. False uses
configured phase allocations. There is no artificial 65,536 output ceiling.
Financial admission can refuse the complete reservation, never silently reduce it.
Requested/effective allocations and restriction origins remain inspectable.

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
| Calls, corrections, active planning time | Enforced | Durable warnings through downstream adapters |
| Estimated input, cumulative tokens, context and prompt/response targets | Enforced according to capture | Warnings; complete mandatory context retained |
| Fragments, checkpoints, blocks, operations, nodes, graph/frontier and recursive task counts | Enforced | Warnings; planner-owned schema maxima removed |
| Discovery/retrieval targets, nonproductive-call counts | Enforced | Warnings; exact source identities/pagination required |
| Completed repeated generation strategy | Stops with retained evidence | Warns under separately captured `diagnostic_progress_policy: advisory-v1`; retains rejection, receipts and repeat history |
| Invalid/empty output, cardinality, types, references, uniqueness, expression rules | Reject | Reject; invalid work never becomes accepted progress |
| Business judges, mandatory reviews, permissions, ownership, artifact approval, workspace confinement | Required | Required |
| Financial ceiling, future-review and unknown-usage reservations | Required | Required; no automatic supplement or release |
| Verified provider context/output/endpoint/reasoning capabilities | Required | Required; no protocol/model fallback |
| Per-request timeout, bounded HTTP retries, cancellation | Required | Required; session targets cannot replace request timeout |
| Uncertain dispatch, incomplete HTTP body, effects, invalid/cyclic discovery continuation | No blind replay | No blind replay |
| Execution iteration/concurrency/memory/statements/recursion budgets | Enforced | Enforced independently of planning |

Historical token-only and diagnostic optimization captures keep their original
scope and repeat behavior. Advisory targets never widen repair authority or alter
contracts. Output allocations require physical and financial admission before
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

Content loads when expanded and renders in explicit 32,768-character pages with
search and complete redacted JSON export. Credentials, tokens, cookies, sensitive
headers and endpoint secrets are removed, including decoded JSON string content.
Large documents are neither withheld nor silently truncated. Inspection grants
no planning authority and dispatches no inference.

Protected reads:

- `GET /api/planning/{id}/inspection?after={sequence}` returns up to 200 events,
  the exact next cursor, and independently refreshed call summaries.
- `GET /api/planning/{id}/calls/content?key={exactKey}&offset=0&count=32768&search=...`
  returns text, total characters, exact next offset, completeness and next match.
  The maximum 65,536 characters is a display page size.
- `GET /api/planning/{id}/calls/export?key={exactKey}` returns the complete redacted
  exchange. Use `workflow=true` only for the original chat-owned journal.
- API mutations require the request token/header returned by
  `GET /api/planning/antiforgery`. Blazor uses its protected circuit.

Every read verifies current tenant, exact session, original version-11 journal
owner and request key. Cross-session/request substitution fails. Old records are
not copied or migrated.

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
