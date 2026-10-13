# Validation

All model adapters and hosts used for this correction are disposable and deterministic. No paid inference, live Amazon request or retained-session approval/save/execution occurred.

## Completed focused gates

- 42 UI, actual-browser and corrected-composition/revision checks pass with `-warnaserror` on the corrected build. This includes 36 Designer trace/revision/approval interaction tests, the real keyboard/mobile browser smoke and five new composition/authorization checks.
- All nine new local execution variants pass: complete, one, null URL, missing facts, repeated IDs, unknown ID, unoffered ID, incomplete acquisition and denied writing. Eight passed in the first integrated run; the corrected negative-path fixture passed separately. The initial failed harness attempts remain in `failed-attempts/`.
- 116 existing Planning targeted-revision, approval and recovery regressions pass with `-warnaserror`.
- Existing approval/save/restart lifecycle checks pass in the 52 successful checks of the intermediate UI run. Its remaining failure was the keyboard focus behavior, subsequently corrected and verified by the actual browser test.
- Agent.Server frontend production build passes. The package has no separate frontend unit-test, lint or type-check scripts; Razor interaction tests and the real browser smoke cover the affected UI.
- Exact saved-state continuation validates both receipts under their original schemas, reaches review with three deterministic responses, reproduces the new artifact exactly, and requests nothing after review restart. Original storage hashes remain unchanged.

The full serial solution and final candidate CI results are reported in the final PR report; pending checks are not counted as passed here.

## Independent execution evidence

The complete case independently inspects 39 complete pages / 77 records, 12 offered candidates and ten product visits. The XLSX has the exact three supplied headers plus ten product rows: 33 literal cells, no formulas. Descriptions preserve complete original blocks and row order. A fabricated description fails the unchanged independent oracle.

The one-product case has two rows. The repeated-selection case visits the repeated URL again in selected order and repeats the exact observed row. The null-URL case preserves ten rows with one three-cell blank record and nine visits. The missing-fact case preserves one missing description and one missing price as blanks. Those blanks are established by independent local fixture truth; they are not certified as live absence evidence.

Unknown/unoffered IDs and incomplete acquisition produce no product visits or successful writing. A deliberately unauthorized parent-traversal path produces no file write. Each failure retains normal Browser cleanup. Acquisition errors are never converted into successful missing-data rows.

Every successful case restarts from the encrypted committed terminal result with identical outputs and workbook bytes and zero repeated inference or external calls. Existing recovery suites cover interrupted receipts and unknown-completion protection; no historical uncertain invocation was replayed.

## Measured costs, without an optimization claim

The complete case uses 23 deterministic inference responses: one bounded query conversion, 11 mapping generations and 11 decisions. Typed copies, status predicates, ID lookup, text copying and cell assembly use zero inference. The global product-decision request is 2,568 serialized bytes and contains no raw snapshot. Every request stays within the unchanged 12,000-token fixture admission bound. Mapping validates every page, including candidates outside its generation examples.

Per-case measurements in `local/` record allocations, logical/physical journal size, checkpoint and restore costs, calls and lightweight traces. Cumulative managed allocations include the local host/test process and are not peak resident memory; separate Browser/Document process memory is not measured. No performance improvement is claimed over the semantically incorrect original plan.

Historical v15 lowering, public contracts, server approval authority, runtime limits and receipt semantics are unchanged. Future first-generation model compliance and actual live business acceptance remain unproved. PR #117 remains draft.
