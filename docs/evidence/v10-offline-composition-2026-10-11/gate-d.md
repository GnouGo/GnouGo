# Gate D — remove redundant forwarding

Two deterministic planner checks pass: initial generation reaches review in one call; a disposable structural revision reaches review in two cumulative calls. Both use the actual emitted request, zero automatic repairs and zero external invocations. Accepted requirements and the serialized baseline session remain unchanged.

The shared read-only test catalog now applies Document's existing successful-write schema builder, matching actual MCP discovery. Previously it exposed the nullable CLR error-result schema, unlike the producer's successful structured output. No production contract changed.

Equivalent corrected compositions: 26 → 24 tasks, 67 → 67 steps, 33 → 33 sets, 288,679 → 266,864 UTF-8 YAML bytes. v10 already fused the removed forwarding tasks; no executable counter reduction is claimed here. Maximum line length is 290. The invalid retained 18-task plan lacks exact nullable-field reconnection and cannot serve as an equivalence baseline for those necessary additions.

Remaining technical tasks enforce nullable ID branches, singleton cardinality, nested item captures, original page-record projection and the accepted ten-product bound. They use existing typed bindings without inference. The corrected proposal has two extraction stages (search and product pages), a query-to-URL decision, product-ID selection and per-product fact-ID selection. No inference copies typed records, reconnects IDs or assembles cells.

See `gate-d.txt`. Real full-composition execution and independent workbook acceptance are separate subsequent gates.
