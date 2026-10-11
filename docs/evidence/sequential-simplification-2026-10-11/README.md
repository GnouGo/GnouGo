# Sequential simplification — offline gates

Baseline: `81b6b0e9106e3debf25580b34f49634436d0d03f`, PR #117, draft.
No paid inference or historical workflow replay is authorized by this correction.
The retained live completed ten visits and writing but failed independent acceptance;
its result, receipts, approvals and financial reservations remain unchanged.

## Gate 1 — readable checked JavaScript

Fresh sessions/revisions select `compact-bindings-v9`. Historical profiles retain
exact lowering. The existing final-lowering AST now removes redundant parentheses,
uses native literal/shorthand syntax and aliases exact copies of checked locals.
Both declarations and both ordered contracts survive aliasing. Existing producer
projection boundaries and first-present helpers remain necessary and are retained.
No sandbox, permission or resource allowance changed.

Measurements on the identical business fixture: v8 99,671 YAML bytes; v9 99,319.
On the ordered-check fixture: 2,648 → 2,638 bytes, unchanged execution/failure boundaries.
Scalar selection succeeds beside eight 1.25 MB observations without importing them.
These are compiler-only comparisons; task-count reductions are measured separately.

Validation: 1,253 Planning tests (including 59 focused compilation/execution checks),
27 expression-contract checks and eight real local Browser/Document scenarios passed
with `-warnaserror`. Local scenarios include exact workbook inspection, repeated
selections, empty collections, incomplete observations, denied writes and fabricated
content rejection.
An intermediate formatter attempt removed required producer-address parentheses;
the local semantic validation rejected it. The final correction preserves that
single boundary without relaxing semantic validation.
