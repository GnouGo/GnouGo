# Compact bindings v4: checked deterministic sequences

Fresh sessions and explicit revisions select `compact-bindings-v4` through the existing approval-fingerprinted option. Omitted, v1, v2 and v3 profiles keep their lowering. Public compiler overloads remain compatible; no saved YAML, receipt, journal or approval is rewritten.

Within one execution phase and scope, consecutive pure sets/projections/assemblies/assertions lower to one `set`. Its compiler-owned `checkedMapping` expression uses a closed constant/return JavaScript sequence and literal intermediate schemas. Each intermediate is checked before evaluating the next value; only externally consumed results are published. The existing expression evaluator preserves exact JSON wiring and shares the existing statement/time/memory allowance across the sequence. Learned mapping scripts cannot access this compiler recipe; `mapping.dynamic` is unchanged.

Fusion stops at effects, inference, branches, retries, error handlers and identity-sensitive references. Values needed by failure preservation keep independent receipts. Normal exports stay on the normal path; real cleanup remains in finalization. Intermediate failures retain their original error code and compiler node location. Compilation does not prove that a branch implies an operation's `requires`: the unchanged runtime assertion is authoritative.

Interpretation inputs use explicit closed views from bindings and extraction `resultType`, not inferred field relevance. Keep full observations and action arguments separately. Extract every independent item, flatten only the declared level, and send only the immediate decision view and explicit shared context to global interpretation. Admission measures the complete provider request; oversized necessary inputs remain errors.

The historical profiles below remain documented for stored artifacts.

# Compact workflow bindings

The previous v3 release selected `options.compilation_profile = compact-bindings-v3` for fresh sessions and explicit revisions. The option participates in the existing artifact fingerprint. Historical requests, graphs, YAML and approvals retain their previous compilation profile; the compiler API defaults to historical lowering unless explicitly opted in.

The TaskPlan still describes business operations and data dependencies. Typed field selections and object/array assembly fuse into checked `set` expressions at final lowering. Copy-only foreach bodies compile to structural collection projections: no per-item workflow invocation, inference or journal entry. These optimizations exclude per-item business conditions, inference, effects, explicit cleanup and prior-iteration state. A loop's entry requirement still executes before its bounds check and projection; a failed entry requirement publishes nothing. Each resulting value still receives complete output-schema validation. Missing paths fail; explicit nulls, duplicates, order and nested arrays survive unchanged.

Real business loops keep their child workflow and run inside an isolated captured scope. Sequential loops use the existing counted form over the captured, schema-checked collection. This prevents both preceding loops' results and the current full collection from becoming part of every legacy iteration snapshot. Existing authored loop executors and their result envelopes are unchanged.

## Original indices and bounded inputs

The v3 profile also fuses pure copies using the existing `index` binding. Final lowering emits a two-parameter structural `map` callback; the existing checked evaluator supplies the original zero-based index. It preserves complete observations, exact scalar representations, duplicates and nulls without per-record workflow invocations. Sole-property assembly/export wrappers may be removed when no sibling checks are lost. Entry guards and declared collection bounds still run; per-item guards, executable work, cleanup and prior-state dependencies prevent this optimization.

Indices identify positions in one captured collection. They are not generated business facts, resource ownership or action authority. Learned mapping scripts still cannot return manufactured scalar indices. Compiler-owned projection syntax remains separate from learned extraction and is validated before execution. Existing expression statements, time, memory and nesting ceilings remain effective; no partial result is published after exhaustion.

Typed projection does not enlarge the allowed input of `mapping.dynamic each`. Its existing host collection bound remains unchanged. Use explicit field bindings for already typed data. Where learned extraction is necessary, retain existing complete bounded producer collections (for example, pages containing records), validate every original item, and explicitly flatten the declared nested result. No automatic batching, truncation or per-batch budget reset occurs. Keep complete observations and authoritative action arguments separately; business selection remains an explicit interpretation followed by deterministic reconnection.

Artifact review must distinguish a TaskPlan's declared `maxItems` from the actual host limit. If an extraction binding is too large, use an explicit revision to a compatible composition; do not execute it merely because it compiles. A source page or necessary compact view that exceeds its own allowance still fails safely.

## Normal exports and finalization

The v2 profile resolves exports from their actual graph dependencies. Direct validated references remain direct. Necessary checked selections and assemblies run after normal producers in normal steps. Related output properties share one checked `set` only when their scope, phase, guards and availability match. Private copy projections may be inlined into that assembly when the final schema preserves their constraints; business task identities, shared consumers and presence checks remain intact. Empty adapters are omitted when the scope already contains normal export work.

Exports depending on actual `always` results remain after those producers, with the existing presence guards. Mixed dependencies wait for both. Failure-evidence preservation remains explicit and ordered before cleanup; a failed workflow publishes no successful business outputs. Normal glue uses normal execution allowance. Genuine finalization and nested preservation work still use the unchanged bounded finalization allowance.

The public compiler overloads preserve their previous behavior: omission uses historical lowering; `compactBindings: true` retains v1. Saved requests with v1, v2 or no profile reproduce their original YAML. Stored v3 requests include v2's normal exports and descriptions; fresh requests now select v4 as described above. No stored workflow, pending request or approval is rewritten.

## Literal descriptions

Fresh v2 YAML carries optional common step `description` metadata. Business steps use their TaskPlan objective; compiler-owned steps receive a static purpose, for example “Collect declared iteration outputs.” `InternalRole` remains compiler metadata. Descriptions appear in step telemetry, streaming events, `gnougo-flow.step.description` OTel attributes and encrypted invocation records. They are never interpolated, included in inference inputs or used for execution, permissions, validation or reconciliation. Even `${...}` remains literal. Absent descriptions remain absent on historical steps and journal entries. The whole YAML still participates in artifact approval hashing.

## Extraction versus interpretation

`extract` structures values actually present in JSON, text or HTML. Fresh runtime bindings add optional `infer_each: true`: one source array and a sole array result field (or a whole-array consumer contract) establish independent extraction. Multiple candidate collections require explicit existing `each` selection. There is exactly one output per input item, preserving ordering, duplicates and nesting. Other bound inputs remain shared read-only context. An absent runtime option preserves historical whole-value semantics; omitted transform mode still means interpretation.

Collection inference uses the existing bounded complete examples and tenant cache. Every actual item and the assembled result validate before publication/cache. Historical bindings retain one generation and one repair; the separate approved adaptive profile uses its existing cumulative runtime budget for global program repair and data-specific specialization. See [runtime mappings](runtime-mappings.md). Missing required data is `CONTRACT_UNSATISFIED`; unknown inference completion still stops for reconciliation. No new executor, planning phase, IR, context ceiling or permission is introduced.

Interpretation remains explicit for comparison, decisions and synthesis, using only the consumer's necessary business view. It is never automatically partitioned.

## Approval and failure handling

Agent.Server, `workflow.plan` and the live harness disable redundant startup confirmation for new requests. Artifact approval and explicit requirement acknowledgments remain separate and mandatory. Explicit `require_external_confirmation: true`, historical workflows, MCP permissions, business questions and observed cookie controls remain effective.

Journal writes remain synchronous. After persistence failure, subsequent saves rethrow the first exception with its original stack. No terminal receipt, replay or cleanup may be inferred from that exception.

## Validation and fresh live

The compilation regressions compare legacy and compact YAML, verify complete typed collections without inference/per-item calls, and check linear collected-result sizes for two successive sequential/parallel loops. Runtime tests cover inferred JSON/text/HTML, ambiguity, cache, repair, missing values and legacy omission. Existing recovery tests assert the original persistence failure and unchanged uncertain-completion behavior.

A fresh Amazon evaluation may use `--max-products 10` for planning and execution. The limit is persisted in its run and cohort manifest; comparison rejects mismatched cohorts. Historical cohorts default to three. Product visits, exact observed spreadsheet values and cleanup checks are unchanged. A fresh revision/hash-bound approval is required before execution; the failed `amazon-r8.yaml` and run are never replayed.
