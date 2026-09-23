# Planner computation inference and causal diagnostics

PR #108 also addresses #109. The reported Designer session rejected `.replace` after `decodeURIComponent(...)` and, during an earlier repair, `.match` after `String(...)`. The built-ins were executable but their result types were not statically modeled. The downstream validator repeated the producer's failure.

## Changes

Flow.Core now infers successful string results for direct `String`, `encodeURI`, `decodeURI`, `encodeURIComponent` and `decodeURIComponent` calls with one declared JSON scalar argument. Nullable scalar unions follow JavaScript coercion; coercion establishes neither presence nor business validity. Unknown inputs, containers, aliases, constructors and unsupported call forms remain uninferred. Lexical bindings, reassignment, dynamic scope and potentially global mutation prevent assuming an intrinsic's identity. Numeric conversion and control-flow inference are unchanged.

Binding and replanning share a compact inference profile. Calculations with unproved complete output contracts still require explicit whole-value validation before projection. Invalid URI escapes, invalid input types and missing captures stop before downstream external calls.

Optional computation diagnostics retain the failing expression, its local origin, known contracts and root producer location. Dependent findings retain their blocking status and point to that producer; independent findings remain distinct. The context survives encrypted session persistence, source-generated serialization, repair prompts, API DTOs and expandable Designer details. Missing context preserves legacy serialized diagnostic shapes and artifact hashes. Schema 8, the planner pipeline, cumulative budgets, FinalReview, workflow approval, permissions and runtime `human.input` remain unchanged.

## Deterministic and published checks

- Flow.Planning: 241 passed.
- Flow: 890 passed.
- Flow.Integrations: 79 passed.
- Agent.Server: 397 passed.
- Warning-free solution build and frozen frontend install/build.
- Planner package, Native AOT smoke and trimmed Server encrypted persistence smoke; the latter checks computation context, tenant isolation and stale revisions.

Regressions cover the three sanitized calculation patterns, successful coercion/capture chains, conservative exclusions, runtime failure before downstream calls, root/dependent diagnostics, whole-value validation, restart before technical repair, stable budgets, absence of technical decision cards, and legacy serialization. Existing decision, binding-prefix, host-failure and approval tests remain passing.

## Local live verification

Chromium exercised the actual Designer and originating Chat against the configured OpenAi `gpt-5.5-2026-04-24` model. The targeted request decodes a percent-encoded name, converts it with `String`, replaces spaces and returns a welcome message. Its only business ambiguity is formal/friendly tone.

| Surface | Mode and answer | Result | Calls / replans |
| --- | --- | --- | --- |
| Designer | Auto preferred selection | FinalReview; scenario passed | 5 / 1 |
| Designer | Interactive option | FinalReview; scenario passed | 4 / 0 |
| Chat | Auto preferred selection | FinalReview; scenario passed | 4 / 1 |
| Chat | Interactive custom text | FinalReview; scenario passed | 4 / 0 |

All four persisted plans contain both `String` and `decodeURIComponent`. Decision histories were visible in their originating UI. Auto cases repaired separate generated binding errors within their existing budgets; no inference failures were converted into business decisions. Chat reports zero estimated cost in its DTO; these figures are not a billing statement.

The exact saved candidate from the reported session `2c7b955fa52643e58099885c543ce753`, combined with its accepted prefix and original catalog, now passes grounded validation with **zero findings** on `48745e4`. This check was read-only; the original session remains stopped at revision 16 with seven calls.

A fresh Designer copy of the complete original request (`fa90e89a7dbb4f0aad98029255cde88a`) passed the computation binding that previously failed, but did **not** reach FinalReview. A later business action required an authoritative `revision.comparison.files` artifact that its bound producers did not establish. The planner stopped with `SEMANTIC_BINDING_BLOCKED` and `GROUNDING_BUDGET_INSUFFICIENT` after eight calls and one semantic replan. Its original limits were unchanged, no computation-inference error recurred, and no artifact provenance or review boundary was relaxed. This remains a separate limitation of that broader workflow.

The live generation binary was `a701103`; subsequent `48745e4` adds conservative identity exclusions. All four successful persisted plans revalidate on the final code. Restarting Server on `48745e4` restored their FinalReview states and the copied request's stopped state without additional model calls or approval. The original failed session also remained unchanged.

Private prompts and model responses remain in encrypted journals. No generated live workflow was approved or executed; scenarios are simulated validation, not external business execution. [Sanitized evidence](evidence/planner-computation-inference-2026-09-23.json) includes the unsuccessful broader reproduction as well as the successful targeted cases.
