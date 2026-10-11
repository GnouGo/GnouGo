# Compact bindings v11: offline evidence

Pure single-use preparation moves into consumer inputs while all ordered checks remain. `mapping.dynamic` inference, admission, budgets, receipts and recovery are unchanged. Omitted/v1–v10 compilation remains reproducible. No paid planning or live execution is part of this correction.

## Real generated outputs

- [Full v10 YAML](before-v10.yaml) and [full v11 YAML](after-v11.yaml): the same 24-task comparison fixture, compiled with identical name/catalog/options except profile.
- [Side-by-side JavaScript](comparison.html): actual block values for mapping preparation, projection, flatten, strict lookup and multiple dynamic fields.
- Complete smaller [multiple-input v10](multiple-inputs-v10.yaml) / [v11](multiple-inputs-v11.yaml), and [flatten v10](flatten-v10.yaml) / [v11](flatten-v11.yaml) outputs. The owning tests export these artifacts during their passing equivalence checks.
- [Every retained set](retained-sets.md), with physical producer/consumer addresses and its exact independent responsibility.

| Same TaskPlan | v10 | v11 |
|---|---:|---:|
| Tasks | 24 | 24 |
| Steps | 67 | 61 |
| Sets | 33 | 27 |
| UTF-8 YAML bytes | 266,864 | 261,126 |
| Input expression bytes | 15,601 | 14,510 |
| Immediately unwrapped object literals | 10 | 0 |
| Generic `binding_…` capture-name occurrences | 73 | 0 |
| Nested `checkedMapping` programs | 0 | 0 |
| First-present / strict lookup helpers | 7 / 8 | 7 / 8 |
| Maximum YAML line length | 290 | 290 |
| Maximum expression line length | 172 | 183 |

Longer functional names can increase an individual line while reducing overall expression size. Contracts dominate this unchanged fixture’s YAML; they have not been removed to improve the size numbers. The earlier planner-rendered baseline was 266,921 bytes; the direct compiler comparison uses identical rendering/name settings on both sides. [Raw measurements](compiler-measurements.json).

## Sequential gates

1. [Gate A](gate-a.md): six sets removed, including both mapping preparation groups; multiple-field inputs, shared prefixes, protected arguments and selected-child/whole-envelope distinctions validated.
2. [Gate B](gate-b.md): functional aliases and native projections, with exact numeric values and constant work when adding unrelated large observations.
3. [Gate C](gate-c.md): ordered reference-only check events on existing spans, pre-dispatch failure visibility, committed input reuse and continued unknown-completion protection.

Each gate was validated and committed separately. The subsequent alias refinement removes physical task hashes only from lexical capture names. Physical addresses and original error origins remain intact.

Required checked-object wrappers remain because each existing intermediate contract validates an object. Scope exports, nullable branch refinements, shared computations, conditions and preservation/recovery boundaries are documented individually; none is discarded to meet a count target. Historical YAML, approvals, requests and receipts remain untouched.

## Validation and measurement boundaries

Owning compiler tests cover historical lowering, exact values, null/missing distinctions, first-present behavior, large projections and ordered intermediate failures. Runtime tests cover telemetry, recovery, pre-dispatch rejection and continued cleanup/replay prohibition after uncertainty. Actual local Browser/Document executions use deterministic adapters and independently inspect pages/visits, original action arguments, complete source coverage, XLSX headers/cells/order/nulls, output paths and Browser closure. They are not paid Amazon runs.

`gate-a-local-results.json`, `gate-b-results.json` and `gate-c-results.json` retain focused results and raw local measurements. Whole-runtime allocations include existing engine/journal work; logical and physical journal sizes are separate. The scalar/native evaluator measurement adds eight unrelated 1.2 MB observations: allocation stays 35,440 bytes and statements/materialization/output stay 60 / 5,026 / 2,688. Timing samples are diagnostic, not portable guarantees. Reference-only check event size is independent of unread observations; instrumentation does not change charged statements/materialization/output or resource ceilings.

Final solution, Release/package, Native AOT, published encrypted-recovery and CI results are reported separately on PR #117 for the validated commit. The full local command is `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`; publishing uses an isolated checkout of the same production commit to avoid file-copy collisions. Skill validation uses the existing quick validator. No skipped environment-specific test is counted as a pass.

## Budget and live boundary

The existing campaign command recorded the explicitly authorized extension from EUR 150 to EUR 200. [Before](budget-before.jsonl), [extension](budget-extension.json), [after](budget-after.jsonl): spending/reservations remain EUR 137.52423908271879166629417691, leaving EUR 62.47576091728120833370582309. Request/receipt counts and all eight unknown transport reservations are unchanged.

No historical invocation was closed, resumed or redispatched. Future paid planning needs a new explicit authorization; execution also needs its own revision/hash-bound artifact approval and requirement acknowledgments. PR #117 remains draft.
