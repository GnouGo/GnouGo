# Compiled and bounded runtime mappings

`LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML` is unchanged.

No mapping executor in TaskPlan: typed → JavaScript compiled at final lowering; insufficiently typed → DynamicMappingExecutor at runtime.

Business transforms optionally declare `mode: extract` or `mode: interpret`. Omission retains historical interpretation semantics and serialization. Extraction supplies inputs, an objective and a closed result type. It contains no script, runtime executor name or cache setting. A typed HTML string does not declare the fields within the page. Explicit interpretation remains available for synthesis and costs runtime inference.

Contract closure checks structured bindings before final lowering. Known incompatible contracts, missing dependencies, invalid scope and missing artifact provenance remain errors. Compiler-owned projection bindings lower to checked `set` expressions and output-schema validation. An unconstrained typed value may receive a deterministic consumer-constraint guard (for example a string pattern or collection bound); invalid literals, finite domains and contradictory declared constraints still fail compilation. This never asks the model to enumerate possible runtime observations. An opaque source bound to an authoritative consumer, or an explicit extract transform, may instead defer its shape check to a `mapping.dynamic` runtime binding. Artifact review identifies this inference and its maximum two attempts per invocation. Final approval and host policy still apply.

## Runtime extraction

The executor receives only approved observations, an immutable target and extraction objective. A whole observed value already satisfying the exact target passes through without inference. Otherwise it validates a cached mapping or calls the existing model client: one generation and at most one repair, including regeneration after a bad cache hit. Every result is schema-checked before publication. Failed or unsupported extraction returns `CONTRACT_UNSATISFIED`; provider rejection, budget exhaustion, cancellation and unknown external completion retain distinct failure paths.

Learned JavaScript is one restricted expression. Source scalar leaves are opaque identities inside Jint; scripts can select/reassemble them but cannot return arbitrary business constants. Host helpers support strict JSON parsing, bounded regex captures, trimming, HTML entity decoding, percent decoding, URI resolution against an observed base, and strict JSON-number conversion. Containers and bounded array callbacks are permitted. Statements, assignments, prototype access, arbitrary calls, external access and nondeterministic functions are rejected. Output scalar identities must originate from observed input or an approved extraction helper. This preserves decimal data without conversion to JavaScript doubles; arithmetic remains owned by the existing decimal executors.

Defaults remain host-owned. `m.optional(observedContainer, [path])` can request a declared target default only after verifying absence. A literal or synthetic container cannot establish absence; nulls and invalid intermediate observations fail. Missing values do not become defaults merely because a script omitted them. No target default means failure. This is deliberately conservative for ambiguous text.

Extraction does not synthesize classifications, status labels, counters or null placeholders. Use explicit interpretation or declared deterministic business operations for those results. A nullable field permits an observed null; it does not supply a missing observation. Pattern helpers take quoted JavaScript strings, not regex literals. Rejected output scalars identify their result path so a bounded repair can address the actual requirement.

Jint observes cancellation and bounded time, statements and memory. Shape and origin checks do not prove that a selected title or price means what the user intended. Independent execution oracles remain necessary, especially for text/HTML.

## Persistence and recovery

`IMappingArtifactStore` is a narrow Core contract implemented by the existing encrypted workflow-run store. The engine reuses that implementation when its run store provides it. Identity includes tenant, approved binding/objective, producer contracts, target schema, profile version and source shape. JSON shapes include keys, nested types, nullability and array-element shapes. Raw text/HTML and scripts interpreting embedded strings use conservative content hashes. No observations are stored in the cache.

Every cache hit reruns sandbox and target validation. Incompatible or invalid entries cannot authorize data access or artifact ownership. Only validated scripts are cached. Without trusted tenant identity and persistent storage, reuse is local to that execution.

Durable runs record child inference intents and completion receipts through the existing journal and usage-budget path. A completed malformed response still has a receipt and may use the remaining repair. Unknown completion stops for reconciliation and is never redispatched automatically. Resume and outer retries cannot reset the two-attempt allowance. A non-durable execution has only execution-local attempt accounting; restart recovery requires a durable run store.

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

The complete solution passed with `-warnaserror`: **4,182 passed, zero failed, 12 existing skips**. This includes 1,032 runtime, 825 planning and 595 Server tests. Local product fixtures execute real Browser/Document MCP processes and inspect workbook cells independently, including an extract-mode variant with deterministic inference. They do not establish live-provider success.

Release packages (Core, Planning, Persistence, Integrations, Mermaid and Copilot), the osx-arm64 planning Native AOT smoke, trimmed published Server encrypted recovery, frontend production build and skill validation passed. An earlier solution run exposed a Copilot test fixture disposal race; its isolated rerun and the complete rerun passed without Copilot production changes. [Validation and retained log hashes](evidence/runtime-mappings/validation.json).

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
