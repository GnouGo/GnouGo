# Contract-authorized null mapping facts

Production candidate `ce58f8da`, from `89603b4d`; PR #117 remains draft.

## Correction

Literal null now represents an unavailable fact only when the immutable target
explicitly allows null at that exact result path. The export remains private
until the relevant declaration, references/alternatives and complete target
validate. A nullable parent does not authorize null children; unconstrained or
unresolved schemas grant no permission. Every non-null scalar still requires an
observed origin or approved extraction helper. Missing properties, undefined,
invalid observations and host-owned defaults retain their distinct behavior.

Actual runtime generation instructions now explain the null exception and warn against
unrelated observed values as missing-fact placeholders. No field-name heuristic
or semantic-proof mechanism was added: shape/provenance do not prove relevance.
Independent outcome checks remain necessary.

Existing mapping profile 5 isolates newly learned artifacts from prior cache
entries. Historical requests, receipts, YAML and approvals remain unchanged;
incompatible pinned assignments stop rather than redispatching old inference.
There is no new public contract, helper, executor, compilation profile or limit.

## Deterministic regressions

[31 new cases](../../../tests/GnOuGo.Flow.Tests/Runtime/DynamicMappingNullTests.cs)
cover nullable roots, nested fields, array positions, escaped property names,
local references, applicable alternatives and contradictory constraints. They
retain rejection of invented non-null scalars. A generic 15-page / 610-record
fixture preserves exact facts and null positions, uses one generation and no
repair/specialization, validates every page, reuses the warm cache without
inference and resumes completed receipts without another model call. Old cache
profiles are not reused. Existing default, unsafe-program, cumulative resource,
atomic-publication and unknown-completion tests remain in the focused suite.

**150 focused mapping tests pass with `-warnaserror`.** Skill validation and Core
and Planning Release packages pass. The published Planning Native AOT smoke
passes, including serialized planning recovery, typed patch recovery and adaptive
mapping receipts; see [native output](native-aot-smoke.txt). The published Linux Agent.Server encrypted planning-persistence smoke also passes on this candidate ([CI job](https://github.com/GnouGo/GnouGo/actions/runs/38073360332/job/114275161727)).

## Exact retained offline replay

The failed historical run was read through public encrypted-storage APIs. Its
first returned script, resolved inputs and approved target were unchanged. Only
the pure mapping was executed in a disposable in-memory run, with one saved
response and no provider or MCP transport. The original run was not resumed.

All **15 pages / 610 records** were processed. All **31 output fact records** and
null positions match an independent source-selection check. Source observations
remain unchanged. Cold execution consumes one saved response, zero repairs and
zero specializations; warm execution has 15 cache hits and zero model requests.
This validates the retained extraction, not a completed live workbook.

| Measurement | Cold | Warm cache |
|---|---:|---:|
| Sandbox allocated bytes | 14,615,312 | 14,596,616 |
| Sandbox materialized bytes | 1,873,046 | 1,873,046 |
| Sandbox output bytes | 3,466 | 3,466 |
| Sandbox statements | 686 | 686 |
| Active sandbox milliseconds | 71.1749 | 33.4255 |
| Entire disposable run milliseconds | 794.2258 | 139.1175 |
| Entire process managed allocations during run | 144,477,360 | 86,399,088 |

The unchanged sandbox ceiling is **50,000,000 bytes**, 10,000 statements and
5,000 ms. Process allocation totals include workflow/journal setup, hashing,
validation and inspection outside the sandbox; they are neither peak memory nor
an additional sandbox allowance. The earlier live's 53,055,008-byte failure
followed four responses and is separate historical evidence, not a controlled
before/after allocation comparison.

[Exact measurements and hashes](retained-replay.json) and
[disposable replay source](retained-replay.cs.txt) retain the evidence. Compile the
source in a temporary net10.0 console project referencing Flow.Planning and
Flow.Persistence, then supply the existing workspace and the unchanged
`extraction-producer-revisions-2026-10-10/amazon-r8.yaml`. Its only live-store
operation is `EncryptedWorkflowRunStore.ReadAsync`; all execution and cache
writes use disposable memory. The fail-closed adapter supplies only the first
retained script and cannot dispatch another response.

## Request-budget regression caught by CI

The first candidate (`5e948d6c`) failed the existing untyped consumer-view test: added instructions pushed a complete example beyond its unchanged 12,000-token allowance, before the intended oversized-interpretation rejection. The failure reproduced locally with the full returned diagnostic. Candidate `ce58f8da` shortens the instructions while preserving their meaning. All 17 compaction cases pass again with unchanged inputs, limits and assertions. The original failure is retained separately; no test ceiling was raised.

## Local test-host isolation

A clean worktree inherited the collector's development Kestrel endpoint in a mounted-MCP test. Its unused gRPC listener collided with an existing local service on port 4317. The same test passes with `Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0`; this is the environment override used for final solution validation. No service was stopped, no assertion changed, and no production setting or limit changed. Superseded local suite attempts were stopped and retained as incomplete; they are not counted as passing full runs.

## Validation and live planning status

Full solution validation passed **5,111 tests, with 13 existing skips and zero failures** across 33 assemblies with `-warnaserror`; see [per-assembly results](solution-results.json). All 54 local product-fixture cases pass, including independent XLSX/visit/cleanup checks and expected rejection cases ([results](local-workbook-results.json)). All **29 non-skipped CI checks pass**, with four existing skips; see [candidate CI](ci-results.json). Pricing/currency readiness and the configured local Browser/XLSX probe pass. These gates completed before fresh paid planning.

Fresh session `nullfacts20261010a-amazon-1` reached clean review at revision 7 on frozen candidate `ce58f8da`. Initial revision 3 compiled but review found broad HTML interpretation, unrequested interaction and invalid extraction composition. An explicit structural revision retained requirements and business work; a targeted revision removed fabricated capability flags and unrelated interpretation context. All proposals remain separate evidence. No production planner/repair change or historical replay occurred.

Final planning totals: **5 logical calls, 6 transport attempts, 1 automatic repair, 2 explicit revisions, 2 discovery reads**, 49,468 input / 16,313 output tokens, €0.6537669713372970094950749845 and 517,725 ms active planning. The campaign upper bound is **€127.62158578606425266549552572 / €150**, including five unchanged historical unknown reservations. Full accounting is retained in the exact [session inspection](amazon-planning-r7.json).

The user explicitly approved revision 7 and all five requirement IDs. Approval persisted as revision 8 with the same artifact hash. The single execution is complete; see the result below. Historical approvals were not reused. The independent retained mapping replay and local XLSX evidence are not reported as a live workbook result.

## Authorized live execution: failed identity reconnection

The approved artifact ran once, without changes, on the frozen candidate. It acquired **4 complete home-page observation pages / 153 records**, HTTP 200, in one acquisition attempt. Mapping processed and validated all four pages. There were **three mapping calls: one generation, two global program repairs, zero specializations**; one further interpretation call selected the search control. All four requests have verified receipts. The public encrypted-run API confirms terminal `failed` status, completed finalization, 16 normal steps and 1 finalization steps; see [durability check](execution-durability.json).

The final learned script shortened the opaque observed reference with `m.text(r.reference, ":(record:.+)$")`. It emitted `record:13`; the original reference was `9a3574379e43411eac69443f958e7ac6:record:13`. Selection returned the offered shortened key correctly. The first lookup therefore passed, while the second lookup against original `reference` values correctly failed:

```text
CONTRACT_UNSATISFIED: A selected identity is absent from the lookup source.
workflow main; step n_8b14d33e667c7b2f; compiled_node v16
```

This is an identity-preservation failure in learned adaptation, not a null rejection, memory exhaustion, Browser timeout or uncertain provider completion. A source-derived substring can satisfy scalar provenance while failing exact identity reconnection. The declared string schema alone does not require equality to its source reference. The runtime safety check remained intact and prevented action on the wrong reference.

**No search submission, product visit, product extraction or workbook writing occurred.** The workflow closed Browser; the independent cleanup probe observed `No active page`. The unchanged E2E oracle returned `workflow_execution_failed` and `workbook_missing`. Consequently the null correction is still supported by offline retained-product evidence, not by a successful fresh product extraction.

Mapping resources remained below existing limits: **3,427,592 allocated bytes**, 860,553 materialized bytes, 89 output bytes, 196 statements and 27.3078 ms active sandbox time. Three complete example pages were packed (one omitted from generation examples but still executed); the recorded mapping request size was 53,571 bytes. All invocation allowances remained cumulative.

Execution used **4 logical calls / 4 transport attempts**, 60,237 verified input / 3,981 output tokens, **€0.3732496228591711775667761115**, and 92,320 ms workflow time (93,402 ms including the independent oracle). Planning remains separate: five calls and €0.6537669713372970094950749845. Combined fresh planning/execution cost: **€1.027016594196468187061851096**. Campaign upper bound: **€127.99483540892342384306230182 / €150**; five historical unknown reservations remain **€6.5016147947905255187922165013**, with zero new unknown attempts.

[Execution evidence](amazon-execution.json), [exact accounting](execution-accounting.json), [approval](amazon-approval.json), [readiness](execution-readiness.jsonl), and [execution log](execution-log.txt) retain separate facts. Full observations, issued requests and durable receipts remain in the existing encrypted stores. Post-run inspection uses only public read APIs ([inspection source](inspection-source.cs.txt)); it performs zero inference/MCP calls and never resumes the workflow.

Minimal next correction proposed, **not applied to the approved artifact**: preserve opaque candidate identities verbatim (`key: r.reference`), compact only decision facts, and add a generic regression showing selection reconnection retains the exact original identity. Do not append guessed prefixes, weaken lookup equality, or grant authority to derived strings. No retry, new workflow or production change was made after this failure. PR #117 remains draft.
