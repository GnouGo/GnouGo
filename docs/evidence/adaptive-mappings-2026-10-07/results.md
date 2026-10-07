# Adaptive independent mappings: deterministic and live evidence

Frozen production/harness: `05d1321634d94742f10e56896dcb2eb3a9e5271b`; baseline: `49e9db9635fefb2990c959dc77fec0b68acdd1d9`. PR #117 remains draft. No new executor, TaskPlan representation, planning phase, permission or larger limit was introduced.

## Implemented behavior

Fresh approval-fingerprinted `adaptive-each-v1` artifacts enable the optional compiler-owned `adaptive_each` binding. Independent extraction first tries compatible cached mappings or one generic generation. Complete examples are optional beyond one fitting initial item; shape diversity does not force an oversized request. The real failing item is mandatory for specialization and takes precedence over optional examples or previous-script context.

Every original item is evaluated. Successful values remain private and unchanged while unresolved items are grouped by structural shape and stable failure cause. Specialization processes only those groups, including singleton groups when necessary. Repeated identical unsuccessful candidates stop. All calls, sandbox statements, allocations and deadlines remain cumulative; adaptive artifacts require an explicitly configured finite runtime budget before any external action. The live host uses its existing 30-minute execution ceiling together with the unchanged encrypted campaign spending gate. It does not borrow the planner's eight-call budget.

New cache entries are published only after complete result validation. Keys include objective, target, producer, tenant, approved context, item shape and execution profile; content-dependent scripts retain conservative content matching. Every hit is revalidated. Existing invocation controls pin cache assignments and specialization indices, while issued requests and durable receipts preserve restart accounting. Unknown completion still blocks replay. Historical bindings and cache profiles retain their two-attempt behavior.

## Deterministic validation

| Check | Result |
| --- | --- |
| Focused mapping suite | 95 passed; baseline 77 |
| Planning suite | 1,062 passed |
| Full solution with `-m:1 -warnaserror` | 4,771 passed, zero failed, 12 existing skips, 33 suites |
| Frozen-candidate CI | 29 passed, four intentional skips; green before paid dispatch |
| Release packages | Core, Planning, Integrations and Persistence passed |
| Native AOT | Planning smoke passed, including three specialized mappings and committed receipt replay |
| Published encrypted recovery | Trimmed/self-contained Server smoke passed; public journal schema unchanged |
| Browser/Document local execution | Existing fixture variants passed with independently inspected workbook cells |
| Planning skill validation | Passed |

Regressions cover homogeneous warm-cache extraction, mixed-shape groups, singleton specialization beyond two calls under an explicit budget, exact budget exhaustion, mandatory failing-item packing, oversized context/items, defaults/nulls, aggregate rejection, no-progress rejection, cancellation, cache compatibility/isolation, restart and unknown-completion safety. A finite-budget preflight test places an external operation before the mapping and verifies zero external calls when the budget is absent.

The retained-scale regression contains **53 pages / 1,504 records**, distinct page/record shapes and long references. It checks one complete request within 96,000 conservative sizing units, one generic generation and exact ordered output from every item—including records absent from the examples. Successful specialization and cache/recovery behavior are deterministic evidence, not claims about this live provider run.

The final full-solution run contains the last production code; the subsequent test-only cancellation addition passed the complete focused mapping suite and frozen-candidate CI. [Validation counts and log hashes](deterministic-checks.json), [CI results](ci-frozen-candidate.json).

## One fresh Amazon run

Run `adaptive20261007a-amazon-1`, maximum ten products. Configured pricing/currency readiness and all non-skipped CI passed before dispatch. [Manifest](manifest.json), [readiness](provider-readiness.json), [manual review](amazon-review.md), [approval](amazon-approval.json).

Revision 2 compiled but failed manual review: it sent whole snapshots into extraction, requested non-observed classification labels and used an unobserved selector. One explicit review revision preserved every requirement and business operation. Revision 4 used three independent page extractions and reached approval with zero automatic repairs. The revision/hash-bound gate applied the user's authorization once; the original proposal remains retained.

**Execution failed; no XLSX was produced.** The new mapping path succeeded, then the next global interpretation exceeded the unchanged request allowance:

| Measurement | Observed |
| --- | --- |
| Complete Browser acquisition | 52 pages / 1,474 records; neither capture nor manifest truncated |
| Initial mapping examples | Three complete pages, original indices 0, 1 and 51; 49 omitted from generation only |
| Complete mapping request | 89,722 UTF-8 bytes including conservative 4,096 framing allowance; limit 96,000 |
| Mapping execution | All 52 pages processed and validated; one generation, zero specializations, zero cache hits |
| Shape groups | Nine before generation; none unresolved after the generic script |
| Mapping provider usage | 26,417 input / 628 output tokens; EUR 0.133929 |
| Mapping latency | 15.386 seconds |
| Serialized mapping result | 594,458 bytes |
| Next interpretation | 595,042 prompt characters; 393,504 conservative estimated prompt tokens; rejected before dispatch |
| Workflow | 11 steps started; finalization completed |
| Browser cleanup | Real close succeeded; independent post-close read found no active page |
| Unchanged oracle | Failed: `workflow_execution_failed`, `workbook_missing` |

The legacy telemetry field `gnougo.mapping.request_bytes` contains prompt length (63,412 here). The table uses the stored complete request serialized to UTF-8 plus framing instead. Likewise, telemetry's initial `failure_groups: 9` counts unassigned shape groups before the generic candidate; it does not mean nine failed specializations.

The post-mapping consent view still carries broad texts, links and references unnecessary for that decision. Sampling reduces mapping-generation input, not the assembled business output; adaptive execution must not discard that output silently or partition a global decision. The next correction should make that consumer's declared business view narrower while retaining complete source observations and exact action references separately. No runtime limit or source-grounding rule was relaxed to hide this remaining composition problem.

Consent interaction, product visits, later mappings and workbook writing were not reached. There was no external-completion uncertainty in execution: the oversized interpretation has a durable `LLM_BUDGET_EXCEEDED` result with `dispatch_status: not_started`, permitting normal cleanup. The invocation was not replayed. [Sanitized execution and mapping receipt](live-execution.json), [durable journal measurements](journal-metrics.json).

## Accounting and limitations

| Stage | Logical calls / physical attempts | Verified input / output tokens | Verified cost | Latency |
| --- | --- | --- | --- | --- |
| Planning, including one review revision | 3 / 4 | 37,079 / 11,839 | EUR 0.479692 | 514.243 s |
| Runtime provider inference | 2 / 2 | 26,540 / 775 | EUR 0.138388 | 37.056 s execution |
| Total | 5 / 6 | 63,619 / 12,614 | EUR 0.618081 | 551.299 s |

Planning used two discovery reads and zero automatic repairs. One planning transport attempt has unknown usage and retains EUR 1.298287 of conservative reservation. Runtime accounting includes URL construction and mapping generation; the rejected interpretation made zero provider attempts. The runtime scope counted three admissions, including that refused request, and did not refund or reset them.

Campaign upper bound: **EUR 99.356868 / 150**, comprising EUR 95.451828 verified cost and EUR 3.905040 retained reservations. The increase from EUR 97.440501 is EUR 0.618081 verified cost plus the new EUR 1.298287 reservation. Historical unknown reservations remain unchanged. [Six-slot report](live-report.json): **0/1 attempted execution passed; five slots unexecuted**, not a completed six-run evaluation.

No code-review evaluation, cohort expansion, historical replay or GitHub feedback publication occurred. Historical benchmarks, including 33/33, are unchanged. Deterministic gates and successful large-collection mapping do not establish Amazon E2E success.

## Reproduction

```sh
dotnet test tests/GnOuGo.Flow.Tests/GnOuGo.Flow.Tests.csproj -m:1 -warnaserror --filter FullyQualifiedName~DynamicMapping
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -m:1 -warnaserror
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

The already-started live is inspectable only; never execute its approval file again:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace "$GN_WORKSPACE" --campaign schema-portability-20261002 --run adaptive20261007a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --workspace "$GN_WORKSPACE" --campaign schema-portability-20261002 --cohort adaptive20261007a
```

Full observations, original requests, receipts and accounting remain encrypted in the campaign/run stores. Published evidence omits page contents and credentials. Future live work requires a fresh identity, frozen manifest, current readiness and concrete artifact review.
