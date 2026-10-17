# Fresh Amazon validation: planning blocked, execution not started

The Browser correction is validated deterministically. The fresh live run **did not reach artifact review or execution**. No live snapshot recovery, product visits, XLSX values or cleanup can be claimed for this run.

- Candidate/harness: `45473fdafe19da1e770c48c05759df1b516faabf`, clean isolated checkout.
- Cohort/run: `snapshotlive20261007a` / `snapshotlive20261007a-amazon-1`.
- Original prompt, ten-product maximum, existing permissions and inference ceilings preserved. The oracle reader accepts the new coherent snapshot format; all existing content, visit, completeness and XLSX assertions remain.
- [Frozen manifest](manifest.json), [complete six-slot report](cohort-report.json). The other five slots were not dispatched. No code-review evaluation or cohort expansion occurred.

## Planning results

| Stage | Result |
| --- | --- |
| Revision 2 | Three non-boolean `requires` values. The proposal also used raw HTML rather than the new complete-acquisition contract. |
| Revision 4 | Complete acquisition selected, but four `each.input` names did not match bound collection inputs. Review also rejected LLM-derived completeness and unobserved extraction fields. |
| Revision 9 | After the next explicit revision and two automatic repairs, six conditional alternatives lacked matching exports; dependent values were opaque. |
| Revision 12 | Final revision replaced stop-on-failure conditionals with existing sequence scopes. Compilation still rejected an opaque search-selector binding and a non-boolean guard. |

All revisions preserve accepted requirements and cumulative accounting. The feedback uses the existing explicit revision command; production planning guidance, compiler, mapping behavior and runtime are unchanged. [Original provider responses](planning-responses.json), [revision 2](amazon-r2-rejected.json), [revision 4](amazon-r4-rejected.json), [revision 9](amazon-r9-rejected.json), [final proposal and diagnostics](amazon-r12-rejected.json).

The final errors replay through the unchanged compiler with **zero inference**, without modifying the saved session:

- `TASK_INPUT_TYPE` at `/tasks/fill_search_query/inputs/selector`: produced opaque, expected string.
- `TASK_CONDITION_TYPE` at `/tasks/search_amazon_if_possible/requires`: requires a boolean business value.

[Deterministic replay](deterministic-replay.json). The remaining interface is the conditional `updatedPageState` export: its alternatives expose different object shapes. Determining whether a compatible common interface needs an explicit projection or a generic compiler correction is a separate follow-up; this Browser change neither guesses a selector nor relaxes the type check.

A read-only CPU stack sample also located substantial planning time in nested `JsonSchemaInstanceValidator` traversal. [Retained sample summary](validation-stack-sample.json). No validation-performance change was made under this Browser-only correction.

## Accounting and limits

| Metric | Planning | Execution |
| --- | ---: | ---: |
| Logical model calls | 7 | 0 |
| Physical attempts | 8 | 0 |
| Automatic repairs | 2 | 0 |
| Explicit review revisions | 3 | — |
| Discovery reads | 2 | — |
| Verified input / output tokens | 103,082 / 39,667 | 0 / 0 |
| New unknown attempts | 0 | 0 |
| Known cost | EUR 1.513373 | EUR 0 |
| Measured latency | 1,066,897 ms | Not started |

The eight-attempt and two-repair ceilings are exhausted for this run; they were not reset. No YAML/artifact hash was produced, so no requirement acknowledgments or approval command was submitted. There were **zero live acquisition attempts, invalidations, observation pages, mappings, product visits or document writes**.

Campaign upper bound: **EUR 94.327398 / 150** = EUR 91.720645 known + EUR 2.606753 retained reservations for the same two historical unknown completions. No unknown reservation was released, no old artifact changed, and no historical invocation resumed. PR #117 remains draft; this run does not satisfy any live execution gate.

## Reproduce and inspect

Use the frozen source and the configured KeyVault workspace. Read-only commands make no inference calls:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --run snapshotlive20261007a-amazon-1 --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability replay-compile --run snapshotlive20261007a-amazon-1 --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --cohort snapshotlive20261007a --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
```

Collection used the existing `plan --case amazon --max-products 10` path with the run/cohort above, followed by `revise --revision-command <file>` for the retained [r2](amazon-revision-r2.json), [r4](amazon-revision-r4.json) and [r9](amazon-revision-r9.json) review commands. Those commands are historical evidence, not instructions to replay the exhausted run. Any further paid evaluation requires a fresh identity and its existing review gate.
