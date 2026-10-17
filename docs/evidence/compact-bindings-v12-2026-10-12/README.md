# Compact bindings v12 — offline evidence

Baseline: `884bf783`. Sequential implementation: Gate A `da6ff9f2`, Gate B `15d9be09`, Gate C `b2105f28`. No paid planning, provider calls, live workflow, financial changes or historical invocation changes occurred. PR #117 remains draft.

## Actual compiler output

Each pair is generated from the **same** TaskPlan, with only the approval-fingerprinted compilation profile changed. No YAML was hand-edited.

| Case | Before | After | Result |
|---|---|---|---|
| Multiple consumer inputs | [v11](multiple-inputs-v11.yaml) | [v12](multiple-inputs-v12.yaml) | Four declarations become two; all four checks remain ordered. |
| Large observation projection, flatten and strict lookup | [v11](checked-outputs-v11.yaml) | [v12](checked-outputs-v12.yaml) | Native one-level flatten and strict lookup; no capture envelope or `m.select`. |
| Export check before cleanup | [v11](retained-export-v11.yaml) | [v12](retained-export-v12.yaml) | Four declarations become two; both the pre-cleanup validation set and cleanup remain. |
| Complete retained business composition | [v11](before-v11.yaml) | [v12](after-v12.yaml) | Same 24 tasks, physical IDs, 61 steps and 27 sets; smaller programs and YAML. |

The first pair changes this compiler-owned program:

```javascript
const label = { value: data.inputs.label };
const requiredCondition = { value: data.inputs.authorized };
const chosen = { chosen: label.value };
const chosen_2 = chosen;
return { template: data.inputs.pattern, data: { chosen: chosen_2.chosen } };
```

into:

```javascript
const label = data.inputs.label;
const requiredCondition = data.inputs.authorized;
return { template: data.inputs.pattern, data: { chosen: label } };
```

The separate metadata still checks `label`, `requiredCondition`, `chosen`, `chosen_2` at orders 0–3. Both final checks reuse `label`; the unused authorization local must still validate before any consumer dispatch. The complete YAML files retain compiler formatting and schemas.

## Measurements

See [machine-readable compiler metrics](compiler-metrics.json) and [large-array counters](large-array-resources.json).

| Same retained TaskPlan | v11 | v12 |
|---|---:|---:|
| Steps / sets | 61 / 27 | 61 / 27 |
| YAML bytes | 261,126 | 240,061 |
| Expression bytes, including delimiters | 15,652 | 12,965 |
| JavaScript declarations | 113 | 96 |
| Intermediate validations | 113 | 113 |
| Maximum YAML line length | 290 | 290 |
| First-present / strict lookup calls | 7 / 8 | 7 / 8 |

The four-check example falls from 2,434 to 1,901 YAML bytes. The checked-output example falls from 2,270 to 2,018 bytes. The cleanup counterexample falls from 2,756 to 2,278 bytes without moving its check.

A warmed, zero-inference 1.2 MB array replay reduces thread allocations from 8,231,320 to 6,483,824 bytes; charged materialization from 36,839,339 to 17,785,815 bytes; statements from 28,836 to 14,422. Its observed evaluator time was 16.66 → 11.55 ms. Both use the unchanged 50,000,000-byte ceiling. These are one local measurement, not portable latency guarantees.

No set disappears from this unchanged comparison: its eligible input preparation was already inlined by v11. [Every retained set](retained-sets.md) identifies exact producers, consumers and the remaining validation, branch, sharing or recovery duty. Alias removal happens inside existing expressions, with checks at their original point. New output metadata does not make a receipt-bearing set disposable.

## Local execution and independent oracles

The eight final v12 cases and eight unchanged historical v11 cases pass: complete, repeated selections, missing values, one result, empty collection, unknown offered ID, incomplete acquisition and denied writing. The initial comparison log is preserved separately in `local-execution-initial.txt`. Models are deterministic adapters; Browser/Document are actual local MCP components. [Raw measurements and independent verdicts](local-execution.txt) are retained.

For the complete ten-product case, both versions visit all ten products, process 39 complete pages, offer all 12 candidates, preserve exact header/cells/order, close Browser and recover the committed result without another inference/navigation/write. The unchanged independent observed-products oracle passes and rejects deliberately rewritten cells. Typed copying and row assembly use zero inference. Both perform 23 deterministic model calls (11 mappings), 359 invocations, 358 normal steps and one cleanup step; the compact decision input is 2,690 bytes.

The complete run measured total process allocations 7,563,816,584 → 7,256,644,472 bytes, logical journal 56,157,929 → 53,930,524 bytes and fixture-directory storage 30,250,855 → 30,209,894 bytes. Runtime was 19.8 → 29.2 seconds under concurrent build load, so this does **not** establish an end-to-end speedup. Directory bytes include the workbook and encrypted storage, not just payload blocks. Persistence algorithms/checkpoint writes were not changed by this correction.

The missing/empty cases intentionally do **not** pass the strict live business oracle: exact fixture cells and blanks are checked independently, while the unchanged live oracle conservatively rejects unsupported absence claims and a zero-product outcome. These negative verdicts are retained, not recategorized as successful live acceptance. Invalid IDs/incomplete observations/denied writes create no successful workbook and retain cleanup.

## Observability and compatibility

Input checks stay on consumer spans. Output intermediate and final checks use existing workflow spans, with name, local reference, origin, pointer, order and outcome/error code only. Tests compare identical event content for one-byte and 1.2 MB observations, stop events at the first failure, and prove no new events on completed recovery. A warmed disposable probe emitted the same five events / 1,706 serialized bytes for both input sizes. Median additional allocation with its collecting telemetry adapter was 8,200 bytes (small input) and 11,416 bytes (large input); measured execution was 0.134/0.123 ms without/with tracing for small input and 1.160/1.252 ms for large input. These noisy samples include the recording adapter and do not establish a latency guarantee. See [measurements](trace-measurements.json) and [actual metadata-only events](output-check-events.json). No observations, schemas or values enter check events.

Direct-value metadata is explicit (`order`/`source`); historical metadata remains object-only. Output metadata is optional and omitted in historical serialization; its presence changes artifact and durable definition hashes. Historical profile overloads retain their lowering. Ordered aliases cannot evaluate JavaScript or access context; only previously evaluated locals are legal. Coupled or unresolved wrapper schemas remain wrapped. Whole-wrapper comparisons and calls also retain their containers; observable container aliases keep their original isolated evaluation. A four-case regression reproduces the mismatch before this guard and proves v11/v12 identity equivalence afterward.

## Validation status

- Gates A/B/C passed their focused checks; see [A](gate-a.md), [B](gate-b.md), [C](gate-c.md).
- Complete final Planning suite: 1,294 passed, no failures/skips.
- Local v11/v12 Browser/Document: 16 passed, independently inspected XLSX and committed recovery.
- Planning Native AOT publish and execution: passed, including direct checked outputs, exact numbers/nulls and source-generated metadata.
- Full-solution and final candidate CI results are reported in PR #117; [validation commands and publish evidence](validation.md) identify the source candidate and retained logs.
- Initial parallel host build encountered the existing generated-static-assets collision; serial `-m:1` build passed. No assertion, timeout or resource limit was weakened.

This is offline validation only. A later paid planning session and its generated workflow require separate authorization and artifact approval.
