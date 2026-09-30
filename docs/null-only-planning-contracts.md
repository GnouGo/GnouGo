# Null-only contracts in TaskPlan compilation

This correction follows `aacd5e1` on `feat/flow-hybrid-planning-v9`, under [issue #112](https://github.com/GnouGo/GnouGo/issues/112) and [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).

## Cause and correction

Retained session `ec7ee0aa2d1444d8a3b1fbbc7b8f1558` explicitly returns null from two conditional branches. JSON Schema supports `type: "null"`; Flow's shorthand type list does not. `PlanningGraphCompiler.ToFlowSchema` copied that name into YAML, causing local `INVALID_OUTPUT_TYPE` errors after successful semantic compilation.

The existing schema-backed representation now handles both `type: "null"` and `type: ["null"]`: its shorthand type is `any`, but its complete authoritative schema remains attached and nullability is derived from that schema. Runtime validation still rejects every value outside the original contract. Conversion recurses through inputs, outputs, captures, properties, array items and dictionary values. No unconstrained fallback, invented default, empty-string substitution or contract widening is introduced.

Runtime types, MCP contracts, model prompts, TaskPlan, approval protections and storage formats are unchanged. No generated compiler error consumes a model repair.

## Evidence and validation

The [sanitized recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/NullOnlyPlanning/README.md) retains four charged calls and three confirmed responses. Attempt three failed with HTTP 500 and has no completion receipt; its identity, diagnostics and charged reservation remain visible. Recovery replay uses only confirmed receipts from their original pending checkpoints. The unchanged proposal reaches review with zero repairs and no extra response.

Before correction, 15 focused compiler cases and the retained replay failed; the non-nullable-input rejection passed. Tests cover exact schema preservation, independent input/output rejection, required-field absence, conditional branches, root/group/iteration boundaries, captures, nullable versus null-only values, empty arrays, source-generated serialization, deterministic recompilation and approval invalidation. Native AOT smoke includes both branches of a literal-null export.

Local validation passed: 3,305 .NET tests across 33 projects, with five existing opt-in tests skipped; all five Flow packages; and the published macOS ARM64 Native AOT planning smoke, including all eight business scenarios and both null-output branches. These checks produced no warnings. Branch CI results are recorded in PR #113. Local logs—including pre-fix failures and test-harness corrections—remain under `artifacts/null-only-contracts-2026-09-27/`. Original encrypted records and historical reports are unchanged. Repository-instruction cleanup is kept in the separate `8403930` commit.

No paid inference, external workflow or real Copilot task is run. The existing Windows x64 Cmd CI blocker and unverified real Copilot sandbox execution remain separate limitations. After deployment, create a new planning session and explicitly approve its workflow; do not silently resume or rewrite the failed session. Keep PR #113 draft and do not merge.
