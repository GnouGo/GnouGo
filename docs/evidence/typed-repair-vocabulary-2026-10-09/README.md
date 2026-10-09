# Typed repair vocabulary parity

Continue PR #117 from `990f58c5`. Fresh private repair envelope 10 exposes the existing `flatten` and `lookup` bindings in already-authorized value slots. Schema construction, read-only context, authority fingerprinting and patch application share versioned definitions. Historical envelopes 7–9 retain their original schemas and authority. No TaskPlan, graph, storage, compilation profile, runtime mapping, limit or permission change.

## Deterministic evidence

`RepairVocabularyTests` validates the actual emitted request, then executes the repaired YAML with repeated identities and null payloads. The same typed repair is rejected by the historical version-9 schema. Historical schema/authority hashes were collected before production changes at `990f58c5`; recovery preserves issued requests and cumulative counters. An upstream JSON-encoded producer requires an explicit targeted revision when outside automatic repair scope. No implicit decoding or broad repair authority is introduced.

The Planning suite passed 1,207 tests before two additional explicit-revision cases were added; the final focused selection passed 75 tests, including those cases. The planning skill validator passed using the existing isolated Python environment. The two new real Browser/Document execution variants passed: each used two deterministic planning calls and one repair, preserved the complete corrected plan, visited ten products and passed independent workbook and cleanup checks. See [local execution measurements](local-execution.json). Release packaging and the osx-arm64 Native AOT publish/execution passed warning-free on the frozen candidate. The complete solution passed with `-warnaserror`: **4,998 passed, 0 failed, 13 skipped** across 33 test assemblies. Skips are seven Windows-only cases, five opt-in paid Copilot evaluations and one Designer browser case unavailable in this environment. See [full solution results](full-solution.json).

The retained local composition now exercises malformed flatten and reversed lookup operands separately through the actual repair request. Each must require exactly one scoped repair, preserve the complete corrected TaskPlan, execute real product visits, write a real workbook and pass independent cell, path, cleanup and receipt-recovery assertions. Existing negative oracles are unchanged.

## Existing CI failures

The starting commit’s deterministic CI job failed in `CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint`: test fixture disposal raced with a directory writer (`Directory not empty`). Its separate host job failed four existing local workbook variants after planning took approximately 136–137 seconds and exhausted the unchanged two-minute fixture allowance before runtime inference. These are retained baseline failures, not evidence that this change passes CI.

## Live boundary

Only one fresh Amazon evaluation is authorized, with at most ten products and no cookie acceptance/refusal/customization. Configured pricing/currency readiness and real disposable Browser/Document readiness passed on `99cc3dca`. The new run is `repairvocabulary20261009a-amazon-1`; the exhausted `noconsent20261009a-amazon-1` remains untouched.

The initial live proposal compiled but failed the accepted public `products` output contract. A three-slot explicit revision corrected collection assembly without changing business work. Review then rejected broad observation inputs; the user explicitly authorized a limited structural adaptation revision. A later automatic repair correctly refused to modify an out-of-scope producer after the model reintroduced nullable candidate objects. The improved lookup diagnostic identified both operands and the nullable producer. An explicit one-slot producer revision changed only candidate-item nullability. The plan now reaches revision 11 `final_review` with unchanged accepted requirements and no diagnostics.

Planning consumed seven logical calls, eight physical attempts, one automatic repair, four explicit revisions and two discovery reads; verified usage was 73,940 input / 17,838 output tokens, costing EUR 0.802946. No unknown usage belongs to this run. The campaign upper bound is EUR 119.819403 / 150, retaining all EUR 6.501615 of historical reservations. See [planning result](live-revision-11-result.json).

The [concrete artifact review](amazon-review.md) records the compact decision contract, offered-ID validation, deterministic reconnection to original records, actual visits/writer/cleanup and remaining explicit inference. Execution is waiting for revision/hash-bound approval and all three requirement acknowledgments. Browser execution, live XLSX and live cleanup oracles have **not** run. Compilation and deterministic local execution are not live acceptance. PR #117 remains draft.
