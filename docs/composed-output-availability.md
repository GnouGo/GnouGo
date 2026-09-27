# Availability of composed TaskPlan outputs

This correction continues `feat/flow-hybrid-planning-v9` from `4e99e82`, under [issue #112](https://github.com/GnouGo/GnouGo/issues/112) and [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). TaskPlan architecture, public contracts, planning format 10 and execution journal schema 9 are unchanged.

## Retained failure

Session `959c840a609b4572853fe7ebaa9b22e4` stopped after three model calls, zero repairs and no pending request. Both conditional branches explicitly exported a valid `publication` object. The compiler emitted a checked `value.project` selecting `reviewEvent`, then a typed `set` assembling `{status, event}` behind a projection-presence guard. The finalizer success-availability proof recognized `value.validate` but omitted `value.project`, so both output bindings failed with `BINDING_UNAVAILABLE`.

The [sanitized recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ComposedOutputPlanning/README.md) retains the three responses, issued schemas, request identities, effective settings, exact discovered contracts and stopped diagnostics. Original encrypted records and accounting remain untouched. A minimal reproduction without MCP calls also fails before the correction. Of the initial 15 new planner cases, 14 failed before the fix; the existing `value.validate` case passed. The retained host replay reproduced both original findings.

## Correction and safety

`PlanningGraphTopology.FinalizerAvailableOnSuccess` now includes `value.project` and `array.project` in its existing successful-object-envelope proof. The generated graph, projections, guards, export assembly and cleanup order are unchanged. There is no additional model call, prompt change, MCP rule, runtime change or model repair of generated plumbing.

Envelope presence is distinct from payload nullability. A successful projection can contain an explicitly nullable value; missing or invalid fields still fail execution. Existing schema checks, scope boundaries, cycle detection and rejection of unsafe continuations remain in force. No defaults, inferred exports or broadened repair permissions are introduced.

## Deterministic evidence

The retained replay reaches final review using the unchanged proposal and original request schemas, with serialization/recovery between advances. It retains three calls, zero repairs, original request identities, saved settings, charged usage and discovery receipts. Final review adds the usual notices for five uninspected sources and further uninspected pages; those notices were never computed in the stopped session. Approval verification recompiles the same semantic plan, and changing an export invalidates the artifact. No execution is approved.

Seventeen focused planner cases execute root, both conditional branches, reusable groups, nested/captured values and ordered iteration; they assert exact object/array exports, nullable and empty payloads, missing/invalid-field failure and cleanup after failure or cancellation. Renamed tasks and capabilities have the same behavior. Skipped producers, cyclic guards and unsafe continuations remain rejected. Existing scope, sibling-output and scoped-repair tests are retained. Native AOT smoke adds composed exports to its ordered typed-field scenario without removing the original independent assertions.

Full deterministic results, package checks, Native AOT smoke and exact branch CI status are recorded in PR #113. Local logs, including pre-fix failures and corrected replay-test assumptions about final-review notices and the previously unaccepted graph, are retained under `artifacts/composed-outputs-2026-09-27/`. Those test-harness corrections required no production change. No historical evidence is rewritten.

No paid inference, live benchmark, external workflow or real Copilot task is performed. Historical live results remain unchanged. The prior Windows x64 Cmd CI failures and the real Copilot sandbox/command-execution limitation remain separate from this compiler correction; neither is bypassed or claimed resolved.

## Deployment

Deploy the updated planner, create a new planning session and explicitly approve the resulting workflow. Leave the original stopped session and its accounting intact. PR #113 remains draft and must not be merged while required validation or execution limitations remain unresolved.
