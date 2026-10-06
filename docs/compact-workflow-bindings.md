# Compact workflow bindings

New sessions and explicit revisions use `options.compilation_profile = compact-bindings-v1`. The option participates in the existing artifact fingerprint. Historical requests, graphs, YAML and approvals retain their previous compilation profile; the compiler API defaults to historical lowering unless explicitly opted in.

The TaskPlan still describes business operations and data dependencies. Typed field selections and object/array assembly fuse into checked `set` expressions at final lowering. Copy-only foreach bodies compile to structural collection projections: no per-item workflow invocation, inference or journal entry. These optimizations exclude per-item business conditions, inference, effects, explicit cleanup and prior-iteration state. A loop's entry requirement still executes before its bounds check and projection; a failed entry requirement publishes nothing. Each resulting value still receives complete output-schema validation. Missing paths fail; explicit nulls, duplicates, order and nested arrays survive unchanged.

Real business loops keep their child workflow and run inside an isolated captured scope. Sequential loops use the existing counted form over the captured, schema-checked collection. This prevents both preceding loops' results and the current full collection from becoming part of every legacy iteration snapshot. Existing authored loop executors and their result envelopes are unchanged.

## Extraction versus interpretation

`extract` structures values actually present in JSON, text or HTML. Fresh runtime bindings add optional `infer_each: true`: one source array and a sole array result field (or a whole-array consumer contract) establish independent extraction. Multiple candidate collections require explicit existing `each` selection. There is exactly one output per input item, preserving ordering, duplicates and nesting. Other bound inputs remain shared read-only context. An absent runtime option preserves historical whole-value semantics; omitted transform mode still means interpretation.

Collection inference uses the existing bounded complete examples and tenant cache. Every actual item and the assembled result validate before publication/cache. The entire invocation shares at most one generation and one repair. Missing required data is `CONTRACT_UNSATISFIED`; unknown inference completion still stops for reconciliation. No new executor, planning phase, IR, context ceiling or permission is introduced.

Interpretation remains explicit for comparison, decisions and synthesis, using only the consumer's necessary business view. It is never automatically partitioned.

## Approval and failure handling

Agent.Server, `workflow.plan` and the live harness disable redundant startup confirmation for new requests. Artifact approval and explicit requirement acknowledgments remain separate and mandatory. Explicit `require_external_confirmation: true`, historical workflows, MCP permissions, business questions and observed cookie controls remain effective.

Journal writes remain synchronous. After persistence failure, subsequent saves rethrow the first exception with its original stack. No terminal receipt, replay or cleanup may be inferred from that exception.

## Validation and fresh live

The compilation regressions compare legacy and compact YAML, verify complete typed collections without inference/per-item calls, and check linear collected-result sizes for two successive sequential/parallel loops. Runtime tests cover inferred JSON/text/HTML, ambiguity, cache, repair, missing values and legacy omission. Existing recovery tests assert the original persistence failure and unchanged uncertain-completion behavior.

A fresh Amazon evaluation may use `--max-products 10` for planning and execution. The limit is persisted in its run and cohort manifest; comparison rejects mismatched cohorts. Historical cohorts default to three. Product visits, exact observed spreadsheet values and cleanup checks are unchanged. A fresh revision/hash-bound approval is required before execution; the failed `amazon-r8.yaml` and run are never replayed.
