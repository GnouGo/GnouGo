# Executable support for accepted outcomes

Session `cb5a5d2d946246948aaa66597209b6f0` retained its original request, but its generated workflow did not implement it. Two discovery responses omitted requirements and were rejected, consuming two repairs. The third response used a transformation for external work and a constant describing cleanup. Offline replay confirmed that compilation and artifact validation accepted that historical plan. No clarification questions or answers occurred.

The sanitized TaskPlan is retained in `tests/GnOuGo.Flow.Planning.Tests/Fixtures/OutcomeCoverage/retained-placeholder-plan.json`. Its regression executes the old YAML with an injected deterministic model: it returns a success claim without any external operation. That observation demonstrates why structured JSON and a successful Flow result alone do not prove fulfillment of the request. New outcome validation rejects its claimed external implementations.

## Planning behavior

Initial discovery can return `requirements: null`. Requirements and the public input interface must be established before accepting a TaskPlan. Discovery remains bounded, uses persisted receipts, and does not consume a repair merely because intent is not yet frozen. Clarification remains a mutually exclusive action in the same loop.

New sessions use optional `outcomeVersion: 1` metadata. Every accepted requirement declares:

- `execution`: `data`, or an authoritative operation effect (`read`, `write`, `execute`, `lifecycle`).
- `always`: whether supporting work belongs in cleanup.
- `conditional`: whether a branch or possibly empty collection may skip it.

Data outcomes declare both flags false. Each plan response includes `outcomeBindings`, with exactly one entry per outcome: `outcomeId`, supporting `taskIds`, and named root `outputs`. External outcomes require actual operation tasks with matching resolved, permitted effect contracts; returned values are not evidence that actions happened. Data outcomes can reference tasks or root outputs. Discovery and clarification provide no bindings. Repairs retain the host-owned bindings.

The deterministic validator checks identities, reachability through invoked groups, normal/cleanup placement, effect compatibility and unconditional branch/collection coverage. Constant-false branches, empty literal collections and unused groups cannot supply support. Unconditional outcomes require support across alternatives; possibly empty loops require an explicitly conditional outcome. Registered deterministic operations and bounded agents remain supported through their existing contracts. Existing compiler checks continue to enforce types, permissions, artifact provenance, resource bindings and scope exports.

Traversal reuses group/scope results rather than expanding repeated invocation trees. A shared group can support separate normal and cleanup invocations.

The same check runs before final review and artifact approval/saving. Missing support stops with an outcome/task diagnostic and requires an explicit implementation revision when existing repair authority cannot justify an edit. A stopped coverage failure clears stale repair slots and validation results. It never authorizes broad repairs, substitutes a transformation for missing operations, changes budgets or grants permissions. Designer and originating chat display the checked mappings as structural support, separate from observed execution and final approval.

## Compatibility and limits

TaskPlan, PlanningGraph, YAML, runtime and MCP contracts are unchanged. Requirements, proposal and session DTOs gain optional planning metadata; absent properties remain omitted. Historical approvals retain their hashes and already-issued requests retain their schemas. New annotated artifacts include coverage in their approval hash. Repair envelope version 4 also fingerprints it; versions 1–3 retain their original authority rules. Explicit revisions discard annotations and approvals while retaining the prior plan as context and preserving cumulative budgets.

The model still interprets natural-language intent and classifies outcomes. An effect category does not prove that an arbitrary operation meets a prose objective, and two unrelated operations can share the same effect. The review exposes that mapping; no keyword or tool-name heuristic claims to solve semantic equivalence. Likewise, static support establishes neither successful execution nor the truth of a model-generated report. Independent execution observations and existing bounded-agent verification remain necessary. Resource identity checks use available authoritative contracts; no resource semantics are inferred from parameter names.

The affected saved workflow is not migrated, executed or silently repaired. Its replacement needs regeneration and separate approval. Historical benchmark evidence, including the earlier 33/33 campaign, is preserved and does not validate this correction.

## Reproducible validation

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
pnpm --dir src/GnOuGo.Agent.Server/ClientApp run build
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~ComparableContractOverhead --logger 'console;verbosity=detailed'
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -warnaserror -o artifacts/planning-outcomes/aot
artifacts/planning-outcomes/aot/GnOuGo.Flow.Planning.Smoke
```

`PlanningOutcomeExecutionTests` runs generated YAML through the real Flow engine with deterministic MCP adapters and disposable directories. Independent assertions verify exact file contents, reuse of the produced path, cleanup, partial failure and permission denial. This is local deterministic execution, not an actual MCP process or live provider campaign. The comparison test reports three repetitions of the compatibility and outcome contracts on the same candidate code, using identical scripted inputs and execution oracles. Input tokens are conservative estimates; provider usage remains unknown. Host timing excludes network inference and is not a live before/after latency result.

The existing browser smoke now checks outcome review after explicit clarification, keyboard expansion, reload, mobile width and separate approval. Use the browser and published encrypted-storage commands in [clarification validation](planning-clarification.md), with `GNOUGO_CLARIFICATION_BROWSER_OUTPUT=artifacts/planning-outcomes/browser`. The published persistence smoke also checks outcome metadata and tenant isolation. Run Release package checks for affected Flow libraries sequentially with publish steps. No paid evaluation or external repository execution is part of this correction.

Sanitized [reproduction evidence](evidence/planning-outcomes/reproduction.json), [contract measurements](evidence/planning-outcomes/contract-comparison.json) and [validation results](evidence/planning-outcomes/validation.json) are retained separately from historical campaigns.

## Measured impact

A deterministic comparison on the candidate code uses three interleaved repetitions per contract, after one warm-up pair. It compares the compatibility response contract with the outcome contract; it is not a baseline/candidate provider campaign.

| Metric | Compatibility contract | Outcome contract |
| --- | ---: | ---: |
| Successful execution oracles | 3/3 | 3/3 |
| Planning calls per run | 1 | 1 |
| Discovery reads / repairs | 0 / 0 | 0 / 0 |
| Estimated input tokens | 5,927 | 6,270 |
| Host planning median / p95 | 8.84 / 10.33 ms | 7.67 / 7.69 ms |
| Local execution median / p95 | 0.77 / 0.79 ms | 0.78 / 0.97 ms |
| Total measured median / p95 | 9.64 / 11.10 ms | 8.46 / 8.65 ms |
| Provider input/output tokens and latency | Unknown | Unknown |

The added response contract costs 343 estimated input tokens in this small case. Timing differences at this sample size are host noise, not an inferred provider speedup. The existing retained-catalog tests still fit their 24,000-token ceiling after closed discovery navigation is compacted; source receipts and exact operation contracts remain unchanged. The saved failure's three calls/two repairs/zero discovery reads are retained historical evidence, not pooled into this comparison. Four additional local filesystem variants pass independently, including failure and denial.
