# Minimal ID selection and deterministic reconnection

The implementation adds a typed `lookup` binding and retains the existing compiler, adaptive mapper and budgets. Repeated selected IDs produce repeated original records, in order. The compiler does not select relevant fields: the extraction `resultType` and explicit input bindings define the decision view.

The deterministic gate passes. The one authorized Amazon execution **failed before product selection**, during runtime extraction. No workbook was produced. Browser cleanup completed with a verified receipt. PR #117 remains draft; this evidence does not satisfy its six-run execution gate.

## Source and compatibility

- Starting source: `39581420881562fa8fe21c233d49eda28a02cffc`.
- Frozen live source and harness: `3fc9c51dec5176066772802798e560ef61d5a046`.
- Final correction: `c54463fae2f9a4d7827b7aa627e27a3c9243b8c7`.
- No new executor, compilation profile, lookup framework or runtime mapping algorithm. Existing-plan lowering and historical artifacts remain unchanged.
- Lookup uses two operands and a literal identity field. Unknown selections, ambiguous source identities and invalid identity values fail atomically. The helper is compiler-only; learned mapping scripts cannot invoke it. Selection does not confer ownership or action permissions.
- Producer identities remain authoritative. The fallback regression attaches original collection indices before selection, preserving duplicate business values. No content hashing or identity normalization occurs.

The final correction restores an existing tested guidance phrase, supports homogeneous literal identity unions found by the new Native AOT smoke, and rejects fractional JSON identities that would otherwise round to safe integers. It was validated separately from the frozen live. No second paid run was dispatched on that correction.

## Deterministic evidence

The scale regression executes real Flow with deterministic model responses: **54 pages, 1,578 records, 147 candidates**, long original URLs, repeated selections and a relevant candidate outside the mapping examples. Every candidate reaches the global decision; action-only fields remain absent. Both lookups use zero inference. Offered-ID validation precedes reconnection to original records.

| Measurement | Result |
| --- | ---: |
| Complete serialized global request, including conservative framing | 17,318 bytes |
| Existing input-token estimate | 2,603 |
| YAML | 21,661 bytes |
| Mapping generations | 1 |
| Lookup inference calls | 0 |
| Lookup bindings | 2 |
| Selected records, including one repeated identity | 4 |

Tests also cover empty selections, strict numeric/string identity separation, safe integer boundaries, nullable/nested payloads, branches and captures, cancellation, source provenance, fabricated ownership, historical repair schemas and one-call/zero-repair generation. An existing source record not present in the offered view is rejected. Necessary oversized global inputs still stop before provider dispatch.

The real local Flow/Browser/Document fixture covers consent present/absent, complete observations, actual item visits, independent XLSX cells and cleanup. The two added variants pass. The final solution passes **4,831 tests with 13 skips and zero failures**, including 1,118 planning tests. Release packages, planning Native AOT and published encrypted recovery pass. All non-skipped CI checks on the final code commit pass: **29 successes and four skipped jobs**; see [ci.json](ci.json). The CLI publish uses the existing audit policy for two pinned EF experimental notices; no warning suppression was added. These deterministic adapters are distinct from live-provider evidence. Initial fixture registration and prompt-phrase failures were retained and corrected; a passing subset is not substituted for the final full solution result. See [validation.json](validation.json) for final counts and commands.

These measurements are fixture-specific. They are not a matched before/after speedup against earlier Amazon runs, and shape validation does not establish semantic completeness.

## One fresh live, including failure

Run: `lookupselection20261008a-amazon-1`, cohort `lookupselection20261008a`, ten products maximum. Configured pricing/currency and disposable Browser/Document readiness passed before dispatch. The full solution build began in an isolated checkout before this live; execution did not wait for the broader build.

The first review was not executed. Five explicit revisions were required to replace learned reconnection with typed lookups, retain original observations and correct loop bounds without increasing host limits. The final artifact was reviewed against all six requirements at revision **13**, hash `11acd07b7b8f615bf7522d4e9fcf6ce593deb57f6786f9a25315343862235ae7`. Its approval and all revisions are retained. Approval transitioned the session to revision 14; only this artifact executed.

The reviewed composition contains seven lookup bindings; four executed successfully before product extraction failed. Lookup made zero model calls. The compiled YAML is 189,179 bytes; this change does not claim to solve its overall size. Global decisions select observed references; deterministic reconnection supplies original action arguments. An auxiliary index belongs to the complete offered candidate list before global selection; producer references remain the actual identities. No candidate was renumbered for lookup.

| Stage | Calls / attempts | Input tokens | Output tokens | Cost EUR | Latency |
| --- | ---: | ---: | ---: | ---: | ---: |
| Planning, including explicit revisions | 8 / 8 | 145,351 | 43,725 | 1.808949 | 451.533 s |
| Execution | 22 / 22 | 300,836 | 43,656 | 2.496992 | 764.099 s |
| Total | 30 / 30 | 446,187 | 87,381 | 4.305941 | 1,215.632 s |

Planning used two discovery reads and zero automatic repairs. The eight calls and five review revisions **do not meet the minimal-call goal**. Execution used 20 mapping calls and two global interpretation calls. All new usage has verified receipts; the four historical unknown reservations remain retained.

Execution acquired complete snapshots of 3, 7 and **55 pages / 1,563 records**. The first two extraction invocations validated all their items. Product extraction attempted ten mappings, including nine specializations. Retained diagnostics show unsupported mapping operations and incorrect helper arguments. It then stopped at source index 47 with `CONTRACT_UNSATISFIED: The mapping exhausted its cumulative sandbox allowance`. The invocation reported 48 processed/validated items, but failed atomically and published no collection. No product-selection interpretation, product visit or workbook write followed.

The normal journal recorded 95 started steps and one finalization step. The workflow's `browser_close` completed successfully; the independent oracle then confirmed the browser was closed. Its findings remain **`workflow_execution_failed`, `workbook_missing`**. Receipt completion was verified; there was no new uncertain invocation or reconciliation bypass.

This is an unresolved extraction-generation/allowance blocker, not evidence that product selection or XLSX reconnection passed live. No budget, helper rule, oracle or sandbox allowance was relaxed. No replay, code-review run or cohort expansion occurred.

Campaign upper bound after this run: **EUR 107.202698 / 150**, including **EUR 5.203327** unchanged unknown reservations. Remaining admission headroom is approximately **EUR 42.797302**. The six-slot report retains one failed execution and five unexecuted slots; it reports zero passing oracles.

## Reproduction and retained evidence

Run the focused gate from the final correction:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

Read retained live evidence without dispatching:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace "$GN_OUGO_WORKSPACE" --campaign schema-portability-20261002 --run lookupselection20261008a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --workspace "$GN_OUGO_WORKSPACE" --campaign schema-portability-20261002 --cohort lookupselection20261008a
```

The execute phase used the existing `--review-command` mechanism with [approval.json](approval.json), after reviewing [amazon-review.md](amazon-review.md). Do not rerun that command: this execution has already started and completed unsuccessfully. Any future evaluation needs a new identity and its own artifact review.

[manifest.json](manifest.json) and [frozen-binaries.json](frozen-binaries.json) retain source, configuration, prompt, binary and unchanged oracle hashes. The approved [YAML](amazon-r13.yaml), [TaskPlan](amazon-r13.json), planning responses/revisions, mapping scripts, request sizes, accounting and terminal error are retained here. Original page contents, full requests and receipts remain in encrypted campaign/run storage; this public evidence omits raw observations and credential-bearing navigation data. Historical cohorts and saved artifacts are unchanged.
