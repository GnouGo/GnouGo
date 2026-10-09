# Typed repair vocabulary parity

Continue PR #117 from `990f58c5`. Fresh private repair envelope 10 exposes the existing `flatten` and `lookup` bindings in already-authorized value slots. Schema construction, read-only context, authority fingerprinting and patch application share versioned definitions. Historical envelopes 7–9 retain their original schemas and authority. No TaskPlan, graph, storage, compilation profile, runtime mapping, limit or permission change.

## Deterministic evidence

`RepairVocabularyTests` validates the actual emitted request, then executes the repaired YAML with repeated identities and null payloads. The same typed repair is rejected by the historical version-9 schema. Historical schema/authority hashes were collected before production changes at `990f58c5`; recovery preserves issued requests and cumulative counters. An upstream JSON-encoded producer requires an explicit targeted revision when outside automatic repair scope. No implicit decoding or broad repair authority is introduced.

The Planning suite passed 1,207 tests before two additional explicit-revision cases were added; the final focused selection passed 75 tests, including those cases. The planning skill validator passed using the existing isolated Python environment. The two new real Browser/Document execution variants passed: each used two deterministic planning calls and one repair, preserved the complete corrected plan, visited ten products and passed independent workbook and cleanup checks. See [local execution measurements](local-execution.json). Full solution, package and AOT validation follow on the frozen candidate.

The retained local composition now exercises malformed flatten and reversed lookup operands separately through the actual repair request. Each must require exactly one scoped repair, preserve the complete corrected TaskPlan, execute real product visits, write a real workbook and pass independent cell, path, cleanup and receipt-recovery assertions. Existing negative oracles are unchanged.

## Existing CI failures

The starting commit’s deterministic CI job failed in `CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint`: test fixture disposal raced with a directory writer (`Directory not empty`). Its separate host job failed four existing local workbook variants after planning took approximately 136–137 seconds and exhausted the unchanged two-minute fixture allowance before runtime inference. These are retained baseline failures, not evidence that this change passes CI.

## Live boundary

Only one fresh Amazon evaluation is authorized, with at most ten products and no cookie acceptance/refusal/customization. Readiness, campaign accounting and the concrete revision/hash-bound artifact approval remain required. The last retained campaign upper bound is EUR 119.016457 / 150, including all unknown reservations. The exhausted `noconsent20261009a-amazon-1` remains untouched. No new paid inference has occurred at this checkpoint.
