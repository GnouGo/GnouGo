# Checked enum bindings and complete MCP responses

This pass continues `feat/flow-hybrid-planning-v9` from `deef18c`, tracked by [issue #112](https://github.com/GnouGo/GnouGo/issues/112) and [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). TaskPlan, public contracts, approval/recovery protections, planning format 10 and execution journal schema 9 are unchanged.

## Reproduction

Session `75d3bce36ebc4f5f9297b39b57953cbe` stopped locally on `MCP_REQUEST_SELECTOR_NOT_LITERAL`. Its `t_prepare_review.event` was explicitly constrained to `APPROVE | REQUEST_CHANGES`, a subset of the actual consumer enum. Field selection correctly generated a `value.project` that enforces this contract at runtime. Semantic validation recognized checked `set` and structured `llm.call` results but overlooked the projection.

[Sanitized original responses and issued schemas](../tests/GnOuGo.Agent.Server.Tests/Fixtures/CheckedContractPlanning/README.md) preserve all five responses, including two nullable-field errors and an unauthorized first repair. The latter still leaves the baseline, discovery receipts, limits and repair permissions unchanged. Recovery replay now reaches final review with the unchanged final TaskPlan, five calls and two repairs. It supplies no additional model response, grants no approval and leaves encrypted originals/accounting untouched.

Before correction, the retained replay, eight positive projection-selector cases, two nested transform/field-selector cases, six MCP payload-preservation cases and the full review execution fixture failed. Negative selector safety and genuinely missing required-field cases remained rejected.

## Corrections

Commit `608f406` registers literal runtime-enforced output schemas for `value.project`, `value.validate` and `array.project` alongside existing checked outputs. A selector still needs a required, non-null string enum contained in the consumer's allowed values, a valid static schema and safe control-flow availability. Unsafe continuations, unchecked declarations, arbitrary expressions and direct array indexing cannot establish this proof. The compiler continues generating its existing projections; there is no extra transform, forced literal decision or provider-specific rule.

Commit `f0c7270` replaces progress-field stripping with a deep copy of the complete MCP content. Fields named `progressEvents`, `progress_events`, `progress`, `events` and case variants survive unchanged, including ordinary business data. Telemetry forwarding and real-time/final deduplication remain intact. Missing fields are never synthesized. Error classification and response envelopes are unchanged. The obsolete stripping helper and its removal-name array are deleted.

The unchanged full synthetic review fixture from the [clone-path pass](clone-path-contracts.md) now passes deterministic execution: exact mocked decision submission, actual local clone/fetch/checkout/comparison and real packaged Cmd cleanup. It checks the expected review outputs, three repository consumers, the exact clone/cleanup location and preservation of unrelated files. The historical progress-field failure report and recordings remain unchanged. Permission refusal, partial/absent creation, cancellation and later failure retain their cleanup assertions.

## Validation

Focused checks cover nested transform → typed field → MCP selector execution with renamed capabilities, ordered iteration, empty results, invalid/missing/null decisions, safe/unsafe continuations, recovery, rejected repairs and approval invalidation. Required MCP fields, deep-copy isolation, error envelopes and telemetry deduplication are asserted independently.

Full deterministic .NET/Python results, package checks, Native AOT smoke and exact branch CI outcomes are recorded in PR #113. Local logs, including failed pre-fix regressions and an initial smoke-harness omission of capability bindings, are retained under `artifacts/checked-contracts-2026-09-27/`. The harness omission was fixed by supplying the existing compiled capability bindings; it required no production change. Native AOT smoke retains all eight business scenarios, transforms, source-generated serialization, enum/JSON handling and ordered typed field bindings into an MCP selector.

No paid inference, live benchmark, external review workflow or real Copilot task was run. Historical live results remain unchanged. This deterministic execution evidence does not resolve the existing real Copilot sandbox/command-execution limitation. The preceding Windows x64 desktop Cmd timeout failure remains separately tracked; skipped publishing jobs do not count as validation passes.

## Deployment and recovery

Deploy the updated planner/runtime, then start a new planning session and explicitly approve its generated workflow. Do not silently resume the stopped session, rewrite its approval, or replenish its exhausted repairs. Keep PR #113 draft and do not merge while required validation or execution limitations remain unresolved.
