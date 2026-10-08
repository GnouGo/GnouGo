# Compiled and bounded runtime mappings

`LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML` is unchanged.

No mapping executor in TaskPlan: typed → JavaScript compiled at final lowering; insufficiently typed → DynamicMappingExecutor at runtime.

Business transforms optionally declare `mode: extract` or `mode: interpret`. Omission retains historical interpretation semantics and serialization. Extraction supplies inputs, an objective and a closed result type. It contains no script, runtime executor name or cache setting. A typed HTML string does not declare the fields within the page. Explicit interpretation remains available for synthesis and costs runtime inference.

Contract closure checks structured bindings before final lowering. Known incompatible contracts, missing dependencies, invalid scope and missing artifact provenance remain errors. Compiler-owned projection bindings lower to checked `set` expressions and output-schema validation. An unconstrained typed value may receive a deterministic consumer-constraint guard (for example a string pattern or collection bound); invalid literals, finite domains and contradictory declared constraints still fail compilation. This never asks the model to enumerate possible runtime observations. An opaque source bound to an authoritative consumer, or an explicit extract transform, may instead defer its shape check to a `mapping.dynamic` runtime binding. Artifact review identifies runtime inference and its applicable shared allowance. Historical bindings permit at most two attempts; fresh adaptive independent bindings require a finite cumulative runtime budget before execution. Final approval and host policy still apply.

## Runtime extraction

The executor receives only approved observations, an immutable target and extraction objective. A whole observed value already satisfying the exact target passes through without inference. Otherwise it validates a cached mapping or calls the existing model client. Whole-value and historical independent bindings retain one generation and at most one repair, including regeneration after a bad cache hit. The adaptive independent profile described below uses the existing explicit cumulative runtime budget instead. Every result is schema-checked before publication. Failed or unsupported extraction returns `CONTRACT_UNSATISFIED`; provider rejection, budget exhaustion, cancellation and unknown external completion retain distinct failure paths.

Learned JavaScript is one restricted expression. Source scalar leaves are opaque identities inside Jint; scripts can select/reassemble them but cannot return arbitrary business constants. Host helpers support strict JSON parsing, bounded regex captures, trimming, HTML entity decoding, percent decoding, URI resolution against an observed base, and strict JSON-number conversion. Containers and bounded array callbacks are permitted. Statements, assignments, prototype access, arbitrary calls, external access and nondeterministic functions are rejected. Output scalar identities must originate from observed input or an approved extraction helper. Selection preserves observed numeric JSON values. Ordinary compiled arithmetic uses JavaScript Number; exact business arithmetic belongs to the owning MCP, as described in [numeric compilation](numeric-compilation.md).

Defaults remain host-owned. `m.optional(observedContainer, [path])` can request a declared target default only after verifying absence. A literal or synthetic container cannot establish absence; nulls and invalid intermediate observations fail. Missing values do not become defaults merely because a script omitted them. No target default means failure. This is deliberately conservative for ambiguous text.

Extraction does not synthesize classifications, status labels, counters or null placeholders. Use explicit interpretation or declared deterministic business operations for those results. A nullable field permits an observed null; it does not supply a missing observation. Pattern helpers take quoted JavaScript strings, not regex literals. Rejected output scalars identify their result path so a bounded repair can address the actual requirement.

Jint observes cancellation and bounded time, statements and memory. Shape and origin checks do not prove that a selected title or price means what the user intended. Independent execution oracles remain necessary, especially for text/HTML.

### Independent collections

An extract transform may add `each: { input: pages, output: rows }`. Its `pages` input must be an array; its closed result type has exactly one field, `rows`, also an array. Each page produces one row under the declared item schema. Rows may themselves contain arrays; there is no implicit flattening, filtering, sorting or deduplication. Other business inputs are explicit read-only shared context. Interpretation and operation tasks cannot use `each`; historical compiled bindings retain their behavior when it is absent. Fresh `compact-bindings-v1` bindings infer the same independent semantics for one unambiguous source/target collection pair using optional runtime `infer_each`; ambiguous collections require explicit `each`. See [compact compilation](compact-workflow-bindings.md).

The compiler keeps typed identity-compatible collections deterministic. Other independent extractions lower to the existing `mapping.dynamic` with the approved declaration. The learned expression sees the original `item` and approved shared `context` (`source.pages` remains a historical alias). It cannot access the surrounding collection or earlier results. All items and specializations share statement, active sandbox time, imported-data and allocation allowances. Adaptive evaluations retain these counters across engine entries; inference waiting does not renew sandbox allowances. The complete collection is schema-checked before publication; failure publishes no partial business output and caches no script.

Regex helpers reuse each compiled pattern within that evaluation, so repeated items do not repeatedly allocate identical regex engines. This reuse contains no observations, ends with the evaluation, and retains the same nonbacktracking syntax, timeouts and cumulative sandbox limits.

Adaptive generation considers complete deterministic examples: shape representatives, then first/last items where space permits. Shape diversity is optional: oversized candidates are skipped, and later complete candidates are considered. At least one complete initial item must fit. Historical requests and bindings retain their original packing behavior. Counts explicitly identify omitted items. Repair includes the first failing item and original index. Examples generate code, never establish completeness or factual correctness. Every item is evaluated, including unsampled items. Global comparisons and synthesis need their explicit business operations and are never silently partitioned.

A collection mapped to one object, boolean or scalar remains a **global extraction**, even with `infer_each`. Define each immediate consumer's observed business view with the existing closed extraction `resultType`. Separate consumers can use different views; full observations and exact action references remain available through their original bindings. A candidate array per source item allows an examined item to contribute no matches without an empty wrapper object.

An explicit business binding `{ "kind": "flatten", "items": [value] }` concatenates exactly one typed `array<array<T>>` level before global interpretation. It preserves order, duplicates, null elements and deeper nesting. Outer and inner arrays must be nonnullable; untyped contents, scalars and null inner arrays fail. Source constraints are checked before concatenation, and consumer constraints afterward. Empty arrays contribute nothing. The binding compiles deterministically at final lowering, with no mapping inference or new executor.

For example: complete source pages → independent extraction of `array<array<{label, group, reference}>>` → explicit flatten → global decision receiving selected candidate fields. Do not forward broad `pageFacts`, raw pages or unrelated fields into that view. Keep grouping or references when the decision requires them, and preserve full sources for other consumers. Checked assembly validates declared fields; complete-request admission includes instructions, data, schema and framing. Neither step proves semantic relevance, so independent execution oracles still check missed or invented facts. Still-oversized necessary inputs fail before dispatch, without truncating or sampling the decision.

The existing capability resolver exposes an optional verified input allowance after output reservation. Only exact configured/catalog metadata or aliases supply limits, never fuzzy guesses. `ExecutionLimits.MaxMappingInputTokens` can impose a host ceiling; omission uses 12,000. The complete serialized request is conservatively bounded by UTF-8 bytes plus 4,096 framing units and the stricter provider allowance. Unknown limits, excessive shared context or a mandatory example that cannot fit stop before inference. A host may select its already-approved request allowance; this does not alter campaign/provider ceilings. The live harness retains its existing 96,000 input ceiling.

Before live extraction, verify that the exact deployment declares `MaxInputTokens`, `ContextWindowTokens` and `MaxOutputTokens`. A host or campaign ceiling does not establish provider capacity. `LLM_BUDGET_UNVERIFIABLE` with “No verified input allowance” is a capability-metadata preflight failure, distinct from an unknown completion receipt. Supply authoritative deployment metadata; do not substitute a nearby snapshot, infer limits from successful earlier calls or increase ceilings.

For historical bindings, one generation and one repair cover the entire collection, including invalid-cache recovery. Request receipts, tenant isolation, source/target fingerprints and conservative content hashes remain in force. The collection declaration and profile are part of cache identity. Changing the declaration requires an explicit planning revision and fresh approval; existing YAML and approvals are never rewritten.

Compiled collection selection uses exact physical export paths and the existing structural-expression path. It copies only the requested values, preserving missing-versus-null behavior, order, duplicates and origin. Unrelated loop result envelopes and retained evidence remain unchanged.

### Adaptive independent profile

Fresh planning sessions and explicit revisions select approval-fingerprinted `mapping_profile: adaptive-each-v1`. The compiler adds the optional literal `adaptive_each: true` to runtime bindings; TaskPlan is unchanged. Omitted flags retain historical behavior. Global extraction is never automatically partitioned and retains its historical allowance.

Before any external workflow action, an adaptive artifact requires an existing, configured `LLMUsageBudgetScope` with a finite call, token, elapsed-time or cost limit. Runtime and campaign gates both apply; no planner quota becomes a runtime quota and no new allowance is granted. The live harness uses its existing 30-minute execution deadline as the cumulative runtime inference deadline, alongside the unchanged encrypted campaign spending gate. Hosts without a configured budget return `LLM_BUDGET_UNVERIFIABLE` before execution.

The executor first revalidates compatible cached mappings. It then tries one generic candidate on unresolved items, validates every original item and retains successful values privately. Failures are grouped by structural shape and stable validation cause (including contract paths in diagnostics), ordered by their lowest original index. A specialization sees the actual failing complete item first; optional examples come only from that group. Its result is tested against every member. Remaining failures are regrouped, including singleton groups when necessary. Every call uses the same budget scope; invalid programs, cache recovery and specialization do not receive fresh quotas. Repeated identical failures stop without further inference. Cancellation, provider errors, sandbox exhaustion and uncertain completion are terminal for this invocation, not shape mismatches.

The complete assembled result must satisfy collection-wide constraints. There is no filtering, reordering, partial publication, invented scalar or implicit default. New cache artifacts are written only after complete validation. Cache identity includes the approved objective/binding, target and producer contracts, tenant, shared context, execution profile and item shape, not item position or run ID. Raw text/HTML, content-dependent expressions and incompatible scripts sharing a shape use conservative content-specific entries. Every hit is revalidated against current observations.

Existing encrypted invocation controls pin initial cache assignments and each specialization's original indices before dispatch. Existing child invocation records retain requests and receipts. Recovery recomputes deterministic results using those same assignments, replays committed inference, and restores cumulative accounting. Changed cache contents cannot alter a partially completed invocation. Unknown completion remains stopped for reconciliation. No approved YAML or historical artifact is mutated.

Telemetry distinguishes original/processed/sample/omitted counts, failure groups, cache hits, model attempts and specializations. These counts describe extraction work, not semantic correctness; independent execution oracles remain required.

## Persistence and recovery

Independent extraction uses `item` for the current original element and `context` for the other approved inputs. For an element containing records, use `item.records`; its business input name does not become part of the script path. Both generation examples and repair examples use this shape. The aliases reuse the same imported observed tokens, so defaults, missing-versus-null handling and origin checks are unchanged. No alias exposes the full collection. The historical `source` wrapper and callbacks with local parameters named `item` or `context` remain supported.

Collection cache profile 2 isolates new mappings from profile-1 entries. Issued inference requests and completion receipts retain their original contents and are reused during recovery; a profile change never grants another attempt or redispatches unknown completion. Profile 2 retains one generation plus one repair for the entire invocation. Adaptive collection profile 3 is isolated from those entries.

`IMappingArtifactStore` is a narrow Core contract implemented by the existing encrypted workflow-run store. The engine reuses that implementation when its run store provides it. Identity includes tenant, approved binding/objective, producer contracts, target schema, profile version and source shape. JSON shapes include keys, nested types, nullability and array-element shapes. Raw text/HTML and scripts interpreting embedded strings use conservative content hashes. No observations are stored in the cache.

Every cache hit reruns sandbox and target validation. Incompatible or invalid entries cannot authorize data access or artifact ownership. Only validated scripts are cached. Without trusted tenant identity and persistent storage, reuse is local to that execution.

Durable runs record child inference intents and completion receipts through the existing journal and usage-budget path. A completed malformed response still has a receipt and may use the remaining repair. Unknown completion stops for reconciliation and is never redispatched automatically. Resume and outer retries cannot reset the historical two-attempt allowance or the adaptive shared runtime budget. A non-durable execution has only execution-local attempt accounting; restart recovery requires a durable run store.

## Breaking .NET DSL/API migration

`ValueProjectExecutor` and `value.project` are removed. Retired steps fail document validation before execution or cleanup, including nested bodies and finalizers. Saved YAML, journals and approvals are not rewritten. Regenerate/review/approve a new artifact. Custom executors unrelated to retired names remain supported. Python is unchanged.

Whole-value checking:

```yaml
- id: checked
  type: set
  input: '${checkedMapping("({value:source})",data.inputs.payload)}'
  output_schema:
    type: object
    required: [value]
    properties:
      value: {type: string}
```

Ordered per-item selection:

```yaml
- id: rows
  type: set
  input: '${checkedMapping("({value:m.select(source,[[\"row\"]],true)})",data.inputs.records)}'
  output_schema:
    type: object
    required: [value]
    properties:
      value: {type: array, items: {type: string}}
```

A present null is selected and checked; it never falls through to another path. Empty collections, duplicates and nested arrays keep their structure. Validation is atomic. These recipes are compiler/runtime details, never TaskPlan fields.

URI normalization operates only on observed tokens, never invented hosts or business identifiers. HTML decoding and percent decoding are separate operations. Producer-owned navigation contracts require absolute HTTP/HTTPS URLs with escaped whitespace; null/omission still selects the current page. Host restrictions remain authoritative. Refresh discovery and review regenerated artifacts after the contract change.

## Validation

Deterministic and paid-provider evidence are reported separately. The historical 33/33 benchmark and earlier failed live cohorts are unchanged; no historical result establishes success for this change.

The complete solution passed with `-warnaserror`: **4,194 passed, zero failed, 12 existing skips** on production revision `90cca01d`. This includes 1,038 runtime, 829 planning and 597 Server tests. Local product fixtures execute real Browser/Document MCP processes and inspect workbook cells independently, including an extract-mode variant with deterministic inference. They do not establish live-provider success.

Release packages (Core, Planning, Persistence, Integrations, Mermaid and Copilot), the osx-arm64 planning and Copilot Native AOT smokes, trimmed published Server encrypted recovery, frontend production build and skill validation passed. An earlier solution run exposed a Copilot test fixture disposal race; its isolated rerun and the complete rerun passed without Copilot production changes. [Validation and retained log hashes](evidence/runtime-mappings/validation.json).

There are still 19 executor classes: `DynamicMappingExecutor` replaces `ValueProjectExecutor`. The new sandbox, durable attempts and encrypted artifact contract add runtime code; this change does not claim a net reduction in production lines or an execution-success guarantee from compilation.

Reproduce local checks:

```bash
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Persistence.Tests -m:1 -warnaserror
PLAYWRIGHT_MODULE_PATH=/path/to/playwright/index.mjs dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
pnpm --dir src/GnOuGo.Agent.Server/ClientApp run build
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
```

The smoke now executes restricted HTML extraction and exact decimal copying and checks source-generated mapping artifact serialization. Published Agent.Server `--planning-persistence-smoke <disposable-directory>` additionally checks encrypted mapping persistence, restart, tenant isolation and removal. Use the platform's supported RID.

### Retained live iterations

The real-provider untyped matrix passed **9/9** in `mapping3`, `mapping4` and `mapping5`, on their separately frozen revisions. This includes real MCP transport, cold/warm cache, changed values/shapes/contracts, text/HTML extraction, expected rejection of missing data, and a separately labelled injected repair. These results are not pooled. `mapping5` used profile 2; its retained manifest incorrectly recorded a hardcoded 1, documented in its audit. Subsequent manifests use the runtime profile constant.

The broader `mappinglive3` cohort remains **0/6, incomplete**. Two code proposals were refused before approval (a placeholder report and an output removed by cleanup). The third reached real Copilot but exhausted the final writer's approved allowance; repository checks and the local report were incomplete. Amazon encountered percent-encoded observed product links; the published consumer schema did not expose the URI constraint. The generic correction adds source-backed URI helpers and typed consumer guards, with no site-specific planning rule. Two Amazon repetitions were not started after that defect. [Retained measurements](evidence/runtime-mappings/mappinglive3.json), [failure audit](evidence/runtime-mappings/mappinglive3-audit.json).

`mapping6` on `1685f125` (profile 3) passed **9/9** with one planning call and zero planning repairs. Planning used 4,451 input / 843 output tokens (€0.04236); runtime used nine paid calls, 4,786 input / 4,972 output tokens (€0.15420), plus one explicitly injected non-provider attempt. Warm-cache execution took 78 ms and zero calls versus 6,461 ms and one call cold. The changed-value case reused the mapping in 81 ms. [Complete matrix](evidence/runtime-mappings/mapping6.json).

`mappinglive4` remains **0/6, incomplete**. All three Amazon workflows were executed: two wrote workbooks and cleaned up, but lacked observed descriptions; one stopped at the new URI guard before navigation. The third successfully used dynamic mapping for real product links. Its mobile product URL form also exposes a limitation of the frozen visit oracle; missing descriptions independently fail the content check. One code proposal reached review but was not executed when a harness defect was found: ordinary runtime inference omitted the input-limit gate already applied by the Copilot proxy. Large HTML calls exceeded the 96,000-token allowance, while the €50 spending gate and usage accounting held. These runs remain diagnostic, not acceptance evidence. The same existing token estimator and output limit now gate ordinary runtime dispatch too, without changing host policy. [Measurements](evidence/runtime-mappings/mappinglive4.json), [audit](evidence/runtime-mappings/mappinglive4-audit.json).

`mapping7` on the corrected harness passed **8/9**: the injected-failure repair produced an overescaped speculative multi-format parser. Exhaustion correctly returned `CONTRACT_UNSATISFIED` without caching the script. The next generic prompt correction asks for the current observed format only; source signatures handle other formats. Regexes are never rewritten to make a response pass. [Retained matrix](evidence/runtime-mappings/mapping7.json), [audit](evidence/runtime-mappings/mapping7-audit.json).

`mapping8` on `c09d75e1` passed **9/9** after the current-format prompt correction: one planning call, zero planning repairs, 4,451 input / 810 output planning tokens; nine paid runtime calls used 4,952 input / 1,653 output tokens, plus one separately injected non-provider attempt. Total verified cost was €0.10771. Warm-cache execution took 58 ms with zero calls versus 5,815 ms and one call cold. [Complete matrix](evidence/runtime-mappings/mapping8.json).

`mappinglive5` stopped its first code proposal before approval. A literal discriminator selected nested constraints too late for binding guards, causing unnecessary enum-repair diagnostics for unrestricted runtime strings. The compiler now selects that authoritative branch before binding ports; ambiguous selectors and incompatible types still fail. The proposal's separate invalid cross-scope dependency remains rejected. No saved proposal was modified or executed. [Measurements](evidence/runtime-mappings/mappinglive5.json), [audit](evidence/runtime-mappings/mappinglive5-audit.json).

`mapping9` on `e7229199` passed **9/9**, with one planning call, zero planning repairs and one discovery read. Planning took 13,309 ms (4,451 input / 710 output tokens, €0.03880). Eight paid runtime calls used 4,455 input / 1,862 output tokens (€0.06961), plus one injected attempt. Warm-cache execution took 65 ms with zero calls versus 5,861 ms and one call cold. [Complete matrix](evidence/runtime-mappings/mapping9.json).

`mappinglive6` remains **0/6, incomplete**. Review withheld approval for an extract-mode proposal synthesizing CAPTCHA flags/absence messages and a code proposal without a surviving report writer outside its confined checkout. The second Amazon proposal executed, but its captured HTML required an estimated 144,444 prompt tokens, exceeding the unchanged 96,000-token allowance. That request was rejected before provider dispatch. An ordinary `llm.call` receipt defect incorrectly turned this known admission rejection into reconciliation and blocked workflow cleanup; the oracle subsequently released its test browser. The correction records explicit host-owned pre-dispatch rejection and completed responses before schema validation. Unmarked/unknown failures still require reconciliation, and no saved run is resumed automatically. [Measurements](evidence/runtime-mappings/mappinglive6.json), [audit](evidence/runtime-mappings/mappinglive6-audit.json).

`mapping10` on final production revision `90cca01d` passed **9/9** with one planning call, zero planning repairs and one discovery read. Planning took 14,391 ms (4,451 input / 749 output tokens, €0.03984). Eight paid runtime calls used 4,551 input / 2,125 output tokens (€0.07706), plus one injected non-provider attempt. Warm-cache execution took 57 ms with zero calls versus 7,262 ms and one call cold; changed JSON values reused the mapping in 52 ms. The missing-data case correctly exhausted its two attempts and failed its contract; the independent expected-failure oracle passed. [Final mapping matrix](evidence/runtime-mappings/mapping10.json).

Paid collection stopped at **€35.45389 of €50**, leaving **€14.54611**. The euro ceiling was not exhausted. The broader six-execution gate remains **incomplete** because of the fixed per-request input allowance and rejected business proposals; it has not passed on the final candidate. The receipt correction was verified deterministically, without reconciling or replaying the stopped Amazon run or the historical Copilot invocation. PR #117 remains draft.

For rollout, rebuild/restart Agent.Server and the affected runtime components, refresh discovery after changed producer contracts, and generate/review a fresh artifact. Preserve saved YAML and journals. Existing `value.project` workflows require explicit revision and fresh approval. Do not resume a historical uncertain invocation solely because receipt handling has been corrected.

Mapping profile 4 rejects direct comparison or truthiness of observed tokens: use `m.test(token, pattern)` for text predicates and `m.has` for presence. These control predicates cannot be returned as fabricated business values. The existing bounded attempts and source-grounding checks remain unchanged. [Regression and execution work](compact-observations-and-execution.md).

### Reconnect selected identities outside inference

An extraction result's declared `resultType` controls its decision view; neither compiler nor runtime heuristically decides relevance. Keep action-only URLs, references and other arguments in retained original observations. Global interpretation selects identities from the complete view. Deterministic `lookup` bindings verify that selections were offered and recover the exact original records before actions. Repeated selected IDs intentionally repeat records, with no deduplication. Unknown or ambiguous identities fail atomically.

Use an existing producer key or attach the original collection index before extraction. Indices stay bound to that immutable capture; never use content hashes or renumber after filtering. Lookup is compiler-owned reconnection, unavailable to learned mapping scripts. It grants no permissions, artifact ownership or immunity from producer-side stale-reference checks. There is no change to adaptive mapping, cache profiles, inference admission or cumulative limits.
