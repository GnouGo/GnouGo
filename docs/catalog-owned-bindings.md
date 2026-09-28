# Catalog-owned TaskPlan bindings

This deterministic correction starts from `56408ec` on `feat/flow-hybrid-planning-v9`,
under issue #112 and draft PR #113. TaskPlan, execution, approvals, planning format 10
and execution journal schema 9 are unchanged.

## Retained failure

Session `f7bd56ef0ef94cb0b0df3f73cf34f730` used six discovery calls, a proposal and one
repair: eight calls, one repair, no pending request. The operation used by
`run_project_checks` fixed `runner: "coding"`, but its discovery ports incorrectly
advertised `runner` as required and editable. The TaskPlan supplied `"copilot"`.
The graph's independent `CAPABILITY_BINDING_OVERRIDE` rejection was correct;
omitting the input previously caused `TASK_INPUT_REQUIRED`.

[The sanitized recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/CatalogOwnedBindings/retained-owned-binding.json)
retains all eight responses, original issued prompts/schemas, contracts, seventeen
revision summaries and accounting. Local paths and concrete repository URLs are
redacted. Original encrypted records and previous evidence remain unchanged.
Recording SHA-256: `296a37f7549d2849c54beb348c64db2cc9c2ec82c31f89679bf36482214e2633`.

## Generic correction

- One transient view derives editable ports from authoritative mappings, fixed
  executor inputs and MCP request bindings. Ownership compares decoded JSON-pointer
  segments, including escaped property names; executor options are distinct from
  business request fields. No provider/tool/value rule is involved.
- New descriptions and strict operation schema variants exclude owned input names.
  Resolved operations have no unrestricted alternative. Index-only operations
  remain selectable and receive the same ownership check after exact resolution.
  Common schema definitions are shared; fixed-operation repair schemas expose only
  operations already allowed by their immutable baseline.
- Requiredness recognizes catalog-supplied values. Explicit assignments, including
  identical values, fail at the semantic input location before lowering. Partial
  object assembly may supply unrelated fields; opaque ancestor assignments cannot
  overwrite owned descendants. Invalid/ambiguous bindings fail closed with redacted
  diagnostics. Authoritative schemas, values and receipts are not edited.
- Compilation injects catalog values deterministically. Full effective-request
  validation, conditional requirements, graph conflict checks and approval
  recompilation remain independent protections.
- A diagnosed owned binding permits removal only. Nested removal preserves sibling
  members; unrelated objectives, operations, inputs and ordering stay fixed.
  Rejection preserves the baseline, receipts, identities and cumulative budgets.

The obsolete unused editable-arguments helper is replaced by this shared ownership
calculation. No public DTO, runtime behavior, storage migration or model phase is added.

## Deterministic evidence

Eight initial regression cases failed against the parent implementation. Focused
coverage includes required owned inputs, equal/conflicting assignments, nested
request bindings, escaped pointers, explicit aliases, null values, complete fixed
requests, invalid bindings, renamed runner discovery, scoped removal/rejection,
recovery and changed approval material. Existing safety and business oracles remain
unchanged.

Historical replay through `HybridWorkflowPlanner` preserves the original request
schemas and identities and now reports `TASK_INPUT_HOST_OWNED` at
`/tasks/run_project_checks/inputs/runner`, before graph emission. It does not spend
another call or change the stopped session's accounting.

[The corrected fixture](../tests/GnOuGo.Agent.Server.Tests/Fixtures/CatalogOwnedBindings/synthetic-owned-omission.json)
is explicitly synthetic: the final recorded proposal loses only its `runner`
binding. In a separate new synthetic session with the exact retained contracts and
metadata, it reaches final review in one scripted call, zero repairs and zero
metadata reads, with `runner: coding` in YAML. This session reuses metadata receipts,
not the historical model's inspection selections. It is not a restart, a replacement
live outcome or evidence of successful Copilot execution. Approval recompilation
and invalidation after changing the catalog binding are checked.

## Request-size impact

The following read-only comparison reconstructs newly issued requests from the
same saved states. Original requests remain unchanged and are replayed using their
original schemas. Estimates include the complete response schema, using the existing
conservative estimator; no token limit was raised.

| Call | Original estimate | New estimate | Prompt bytes, before → after | Schema bytes, before → after |
| --- | ---: | ---: | ---: | ---: |
| 1 | 9,794 | 9,531 | 9,006 → 9,006 | 19,606 → 18,817 |
| 2 | 21,574 | 21,500 | 41,876 → 34,094 | 22,077 → 29,636 |
| 3 | 21,579 | 21,543 | 40,997 → 30,006 | 22,972 → 33,854 |
| 4 | 21,533 | 21,455 | 40,499 → 27,785 | 23,330 → 35,812 |
| 5 | 21,452 | 21,560 | 40,258 → 28,098 | 23,330 → 35,812 |
| 6 | 21,554 | 21,587 | 40,562 → 35,648 | 23,330 → 28,344 |
| 7 | 21,547 | 21,574 | 48,061 → 35,454 | 15,812 → 28,499 |
| 8 | 23,244 | 24,430 | 53,152 → 53,079 | 15,812 → 19,442 |

Cumulative estimates: 162,277 → 163,180. Strict per-operation input names grow
the response schema; existing optional-context packing responds within the unchanged
soft target. The large historical repair context would need 24,430 tokens if issued
anew and must stop under a 24,000 ceiling. Historical recovery replay still uses
the original eighth request's 23,244-token schema/context. This pass does not compact repair baselines
or silently increase settings. All existing prompt-budget regressions remain required.
No live generation, reasoning-token or reliability improvement is claimed.

Full deterministic suites, package, serialization/AOT and applicable branch CI
results are recorded in [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).
Local failed intermediate checks remain under the ignored
`artifacts/catalog-owned-bindings-2026-09-28/` directory.

## Deployment and limits

Deploy the updated planner, start a new planning session and explicitly approve its
reviewed workflow. Do not replenish or silently resume the exhausted session.
The existing Windows Cmd CI failure and real Copilot sandbox limitation remain
separate. No paid inference, live benchmark, external workflow or merge was performed.
