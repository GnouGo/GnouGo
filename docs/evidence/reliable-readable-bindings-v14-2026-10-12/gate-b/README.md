# Gate B: scope-aware readable lowering

Production candidate: `f4f9c699`, after mapping commits `3e2414af` and `3ad3746b`. The compiler comparison uses identical TaskPlans and contracts; no composition repair is hidden in the measurements.

## Cause and correction

V12 simplified flat declarations before v13 supplied branch-local contracts. Standalone lexical expressions were only formatted. Pure-call integration reconstructed `outputs` and physical-step envelopes, then selected fields back out of them.

V14 gathers the checked scopes first, simplifies each closed block from the inside out, retains removed declarations as ordered alias checks, and recomputes scope qualifiers. Closed unconditional IIFEs splice at their single evaluation point. Conditional blocks remain conditional. Required selected paths can bypass synthetic branch envelopes; optional, whole-object, presence and ambiguous observers retain historical reconstruction. Pure-call captures still validate the original input schema.

The runtime evaluator and expression metadata are unchanged. Profile v14 is approval-fingerprinted. Omitted/v1–v13 and existing compiler overloads retain their behavior. Read-only replay recompiled the saved approved r8 artifact under v13 **byte-for-byte** and verified its original approval before generating v14 evidence. It never advanced the historical session or engine.

## Actual compiler output

These are complete generated YAML files, including every check, not design sketches:

| Comparison | Before | After | Extracted complete programs |
|---|---|---|---|
| Checked predicates in acquisition preparation | [v13](comparison-v13.yaml) | [v14](comparison-v14.yaml) | [v13 JS](checked-predicate-v13.js), [v14 JS](checked-predicate-v14.js) |
| Nullable strict-lookup branches integrated into a consumer | [v13](comparison-v13.yaml) | [v14](comparison-v14.yaml) | [v13 JS](nullable-lookup-v13.js), [v14 JS](nullable-lookup-v14.js) |
| Pure-call result integrated into checked status/output preparation | [r8 v13](live-r8-v13.yaml) | [r8 v14](live-r8-v14.yaml) | [v13 JS](pure-call-v13.js), [v14 JS](pure-call-v14.js) |

Additional complete small fixtures cover [controls](control-v14.yaml), [nullable values](nullable-branch-v14.yaml), [multiple consumer inputs](multiple-inputs-v14.yaml) and [checked outputs](checked-output-v14.yaml), with their v13 files alongside.

For example, the first program previously constructed:

```javascript
const predicate1Result2 = {
  value: predicate1Result1.value !== null
};
return {
  n_92b46a869b246c91: predicate1Result1,
  n_496ac96c9b178654: predicate1Result2
};
```

The actual v14 selected branch is:

```javascript
const selection1SelectedValue = (data.steps.n_62737a68773ca42a.response).observationSnapshot;
const selection1SelectedValue_2 = selection1SelectedValue !== null;
return {
  selectedValue: selection1SelectedValue_2
};
```

The surrounding expression selects `selection.selectedValue` directly. Every former intermediate check remains in metadata. In nullable fact resolution, actual v14 code now contains:

```javascript
const flattenedDetailCandidates_4_0 = detailCandidates_4_0.flatMap(item => item);
const selectedDescriptionReferenceIdList_4_0 = [selectedDescriptionReferenceId_4_0];
const requiredCondition_4_0 = m.lookup(flattenedDetailCandidates_4_0, selectedDescriptionReferenceIdList_4_0, "reference") !== null;
```

The original-record lookup follows that assertion; neither lookup is replaced by filtering or a first-match search. Names remain longer than the design sketch where stable collision suffixes or original checked capture keys are required. The [retained materialization ledger](retained-sets.md) explains those limits rather than claiming zero wrappers.

## Structural measurements

See [AST metrics](compiler-metrics.json). JavaScript measures distinct expression programs, so duplicated skill-export serialization does not inflate it. The wrapper counter counts singleton `value`, `outputs` and physical-ID objects; it does **not** count every business object. Technical declarations count `checkedRecord*` and `predicate*Result*`. Function depth counts arrow-function nesting.

| Measure | Retained r8 v13 → v14 | Compiler fixture v13 → v14 |
|---|---:|---:|
| Workflows / steps / sets | 9 / 35 / 8 → unchanged | 7 / 24 / 3 → unchanged |
| YAML bytes | 202,763 → 186,221 | 203,908 → 187,214 |
| JavaScript bytes | 21,546 → 17,377 | 20,785 → 13,892 |
| Longest JS line | 172 → 150 | 249 → 136 |
| Declarations | 193 → 175 | 140 → 123 |
| Arrow functions | 104 → 91 | 90 → 74 |
| Maximum function nesting | 3 → 3 | 6 → 4 |
| Counted wrappers | 71 → 9 | 33 → 0 |
| Technical declarations | 98 → 0 | 27 → 0 |
| `m.select` | 14 → 3 | 7 → 0 |

All original validation-origin multiplicities remain. The three retained r8 selections serve genuine alternative paths. No set reduction quota was applied.

## Execution, checks and resource measurements

The local suite passed **11 checks**: compilation comparison, complete/repeated/missing/one/empty selections, unoffered IDs, incomplete acquisition, denied permissions, unfamiliar URL redirects, and the identical v13 resource baseline. It uses actual local Browser/Document components and independently inspects XLSX cells, headers, order, blank positions, writer path, visits and cleanup. Redirects are recorded producer navigation evidence; no URL-path heuristic was added. Successful restart uses committed results with zero repeated inference, navigation or writing.

The [retained workbooks and independent ZIP/XML inspection](workbooks/inspection.json) also verify literal formula-looking strings and relative/absolute writer-path correspondence. The unchanged live oracle deliberately rejects zero-product outcomes and unsupported absence claims in the empty/missing fixtures; their exact nullable/header-only cells pass fixture ground truth, not live business acceptance. No oracle was weakened. Rewritten description cells remain rejected.

The complete local execution has identical **1,089 reached validation origins and outcomes in order**: [trace proof](ordered-trace-proof.json). Events contain names/references/origins/outcomes, not observations or schema copies. Their aggregate serialized size fell from 444,684 to 393,843 bytes. No synthetic checks are emitted during committed-result reuse.

| Single complete local run | v13 | v14 |
|---|---:|---:|
| Actual invocations | 143 | 143 |
| Deterministic inference-adapter calls / mappings | 23 / 11 | 23 / 11 |
| Complete decision request bytes | 2,690 | 2,690 |
| Process allocated bytes | 2,777,743,616 | 2,608,718,920 |
| Elapsed milliseconds | 11,708 | 10,805 |
| Logical journal bytes | 22,409,619 | 20,791,156 |
| Physical encrypted store bytes | 13,444,964 | 13,404,006 |
| Checkpoints | 586 | 586 |
| Checkpoint time, ms | 7,358 | 7,475 |
| Snapshot captures / restores | 286 / 143 | 286 / 143 |

These are single in-process integration measurements, not latency guarantees. They include Browser, telemetry, persistence and local server work. Random local addresses make execution-YAML bytes slightly different from the frozen compiler comparison. There are zero paid/provider calls. Full measurements, failures and recovery results are in [local](local/).

The direct expression regression evaluates actual generated v13/v14 YAML with one byte and 1.2 MB of unrelated observations. Warm success allocates **42,104 → 33,680 bytes**, charging **804 → 463 materialized bytes**; the selected-null branch allocates **42,136 → 33,768**, charging **567 → 286**. These counts do not grow with the unrelated payload. Missing and invalid unused values fail at the same original boundary and produce the same reached-check sequence. First/warm measurements are in [resources](resources/); they are JIT/parser evaluations, not inference-cache benchmarks. The unchanged 50,000,000-byte sandbox ceiling applies.

## Reproduction

`compiler-replay/Replay.csproj` uses existing compiler test friendship and public encrypted KeyVault APIs. It verifies tenant/session/revision/artifact approval, compiles the same stored TaskPlan with its original catalog, and performs no workflow execution or storage write. `--measure <yaml>` computes AST metrics without reading the historical store. Core's generated-YAML regression measures the internal allowance through its existing test assembly; no public visibility was added.

Owning validation: 1,308 Planning tests, 67 final focused fusion checks, four generated-expression resource cases and 11 local integration checks passed with `-warnaserror`. The wider solution/package/recovery/CI results are recorded separately in the final validation report.
