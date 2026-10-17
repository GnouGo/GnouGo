# Final planner stabilization results

## Final execution gate

Frozen candidate `7d5d42b2f4713464bd76589ec1d3302640830f8e`: **33/33 execution oracles**, versus **26/33** baseline. All runs and variants remain in the denominator.

| Scenario | Integration | Complexity | Baseline correct | Candidate correct |
| --- | --- | --- | ---: | ---: |
| `local` | mocked | simple | 3/3 | 3/3 |
| `read_transform` | mocked | simple | 3/3 | 3/3 |
| `nullable_defaults` | mocked | medium | 0/3 | 3/3 |
| `conditional` | mocked | medium | 3/3 | 3/3 |
| `collections` | mocked | complex | 3/3 | 3/3 |
| `protected_cleanup` | mocked | medium | 3/3 | 3/3 |
| `review_french` | mocked | complex | 3/3 | 3/3 |
| `review_distractors` | mocked | complex | 2/3 | 3/3 |
| `files_read` | real_local | simple | 3/3 | 3/3 |
| `files_copy` | real_local | medium | 3/3 | 3/3 |
| `files_loop_cleanup` | real_local | complex | 0/3 | 3/3 |

| Complexity | Baseline correct | Candidate correct |
| --- | ---: | ---: |
| simple | 9/9 | 9/9 |
| medium | 9/12 | 12/12 |
| complex | 8/12 | 12/12 |

## Efficiency

All-run statistics include failures. Latencies are seconds; execution time covers every variant of a generated workflow. p95 uses the nearest rank.

| Metric | Baseline median | Baseline p95 | Candidate median | Candidate p95 |
| --- | ---: | ---: | ---: | ---: |
| Logical planning calls | 1.00 | 1.00 | 1.00 | 1.00 |
| Physical planning attempts | 1.00 | 1.00 | 1.00 | 1.00 |
| Execution model calls | 0.00 | 0.00 | 0.00 | 0.00 |
| Discovery reads | 6.00 | 10.00 | 6.00 | 10.00 |
| Repairs | 0.00 | 0.00 | 0.00 | 0.00 |
| Total input tokens | 4,108.00 | 8,313.00 | 4,040.00 | 8,260.00 |
| Total output tokens | 888.00 | 2,886.00 | 909.00 | 1,936.00 |
| Planning latency (s) | 13.25 | 38.91 | 13.40 | 22.97 |
| Execution latency (s) | 0.01 | 0.46 | 0.02 | 1.34 |
| Total latency (s) | 13.69 | 38.94 | 13.44 | 24.07 |

Totals (planning + execution):
- Baseline: 33 planning attempts, 0 execution model calls; 182,282 input / 37,751 output tokens; EUR 1.800035 estimated cost.
- Candidate: 34 planning attempts, 0 execution model calls; 181,757 input / 32,830 output tokens; EUR 1.667710 estimated cost.

The complete campaign used **EUR 13.473417 / 50**, across 253 physical calls, including all baseline, diagnostic and failed/inconclusive candidate cohorts. Unknown attempts: 0; outstanding reservations: EUR 0.

## Interpretation and validation

The frozen comparison passes every acceptance check: 33 successful nominal executions,
132 correct variant observations, zero safety violations and no incorrect approved
executions in the final cohort. Mocked integration accounts for 24/24 workflows;
actual Cmd MCP plus Flow execution accounts for 9/9. All nine simple workflows use
one logical/physical planning call and zero repairs. No execution invokes a model,
so execution input/output tokens are both zero in both cohorts; reported token totals
are entirely planning usage.

Calls rose from 33 to 34 and repairs from zero to one. The first distractor review
incorrectly declared an ancestor as a local dependency; its issued dependency slot
repaired that binding and all eight execution variants then passed. This is a scoped
repair, not a transport retry or additional LLM phase. Discovery reads are unchanged
per scenario. Input tokens fell 0.3%, output tokens 13.0%, and cohort cost 7.4%.

Median planning latency increased 1.1%; median total latency fell 1.8% and total p95
fell 38.2%. Execution median/p95 increased from 8/462 ms to 16/1,336 ms. The candidate
now performs the real iteration/cleanup executions that failed compilation at baseline;
all variants remain included in the timings. Provider latency and generated output vary,
and three repetitions per case do not support a general statistical speed claim.
The [comparison JSON](comparison-7d5d42b2.json) contains median/p95 metrics per scenario
and complexity, separate planning/execution usage, eligibility and cumulative accounting.

`HybridWorkflowPlanner` shrank from 567 to 389 lines: session transitions, calls,
recovery and approval stay there; prompt/token packing moved to one internal helper,
and discovery uses its existing helper. Required diagnostic dependencies authorize
narrow structural patches and exact producer constraints. Public TaskPlan/PlanningGraph,
storage format 10, runtime and MCP contracts are unchanged.

Validation on this exact production revision:

- `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`: **3,765 passed**, 12 existing
  platform/live skips, zero failures or build warnings across 33 projects.
- **662 planner tests** and **17 benchmark regressions**, including independent
  counterexamples where runtime success fails the business oracle.
- Release planning package and macOS arm64 Native AOT smoke passed.
- [Planner CI](https://github.com/GnouGo/GnouGo/actions/runs/36700573382) passed:
  planner/runtime/host tests, independently packed Flow libraries, Linux Native AOT
  and the published Git MCP contract check.

[Validation hashes](validation-7d5d42b2.json) identify retained local logs. The
[final rows](final-7d5d42b2.jsonl) include the shared manifest, production/harness SHAs,
corpus hashes, pinned model, environment, limits, independent observations and usage.
[Collection and comparison commands](../../../tests/GnOuGo.Agent.Planning.Benchmark/README.md#planner-stabilization-comparison)
reproduce the protocol. [Earlier failures](README.md) remain separate and all charged
attempts count against the same EUR 50 campaign; no paid dispatch is needed after this gate.

The real local workflows do not prove remote review publication or Copilot sandbox
execution; those integrations remain mocked. Three repetitions establish this finite
gate, not universal model reliability. Natural-language intent still needs independent
oracles and approval. Ambiguous structural locations, unresolved producers and cleanup
insertion remain explicit-revision cases rather than broader automatic permission.
