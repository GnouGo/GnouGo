# Designer approval and an offline composition correction

Baseline: `2813d449`. Retained Designer session `d055eaa394ff444c8c494c3a4369f3c9`, revision 4. No paid model call, live execution, financial change, approval, save or continuation of that session is part of this correction.

## Why approval was disabled

The two accepted requirements had not been individually acknowledged. Exact artifact recompilation succeeds; the disabled button was enforcing the intended approval boundary. The UI now places acknowledgment progress, remaining descriptions and a keyboard-accessible link beside the button. It identifies the current revision/artifact and explains that saving does not execute the workflow.

Acknowledgment is never automatic. Same-artifact polling preserves it; navigation, revision/artifact changes and reload clear it. Busy-state protection, exact server-side requirement/revision/hash checks and the existing approved-save recovery path remain intact. The review link focuses the requirements region without navigating away or losing acknowledgments.

## The original plan still needed revision

Compilation proves structural contracts, not business completeness. The retained proposal asks for candidate arrays but declares one candidate object per page; interpretation regenerates original records without strict ID reconnection, copies known data, assembles rows and declares success flags. Cleanup is absent.

- [Historical record/request/receipt hashes](historical-hashes.json) were read through public encrypted KeyVault APIs. The original r4 hash remains `f3f1e46496a6218c24750b8f16238368bd8954df95127143db16a27b8bf3e8c0`; no historical record was changed.
- [Retained v15 YAML](retained-v15.yaml), [corrected TaskPlan](corrected-taskplan.json), [proposal](corrected-proposal.json), and [actual corrected v15 YAML](corrected-v15.yaml) retain the original task identities and discovered contracts. The corrected YAML is **unapproved offline evidence**, not a replacement for the Designer artifact.
- [Generic failing/corrected fixtures](../../../tests/GnOuGo.Agent.Server.Tests/Fixtures/DesignerApproval/) parameterize producer/task names. Regression tests reproduce the original clean compilation and verify the corrected structural revision.
- [Business diff](business-diff.json) explicitly preserves `xlsx_saved`, `first_10_processed`, the required `article` input, `filePath`, `status.saved` and `status.success`. Only `status.rowCountIncludingHeader` and `status.dataRowCount` are removed, as authorized. `cleanup_browser` is the sole added requirement.

The three existing external operations retain their relative order and literal navigation arguments. The existing null-URL branch remains. The search URL still comes from a bounded query conversion. Fixed headers are `Nom`, `Description`, `Prix`; the formerly model-generated destination becomes `workflows/amazon-product-extraction/products.xlsx`, inside the accepted directory. The writer receives `flatten([[headerRow], productRows])`; returned path and status come from its successful response.

Every complete source page is checked and independently extracted into one candidate array. One-level flattening supplies compact ID decisions. Strict offered-candidate lookup precedes strict original-record lookup; original URLs and texts are never regenerated. Descriptions are complete original blocks, including characteristics without headings. Null means an allowed absence, not an acquisition failure. Producer success, snapshot availability, truncation/completeness and writer success are asserted deterministically.

## Disposable structural revision

[Replay evidence](replay-result.json) starts from an in-memory copy of the exact saved r4 session. Both committed responses validate against their original issued schemas. One explicit structural `revise` binds revision/hash, uses the existing requirement-changing path (`preserveRequirements: false`, no `EditablePaths`), and names the limited authorized delta.

Existing discovery uses two deterministic responses to query and inspect Browser closure, followed by one deterministic corrected proposal. The copy reaches review at r8, cumulative logical calls 2→5, zero repairs; the eight-call ceiling and previous accounting remain intact. No provider or MCP operation is dispatched. Restart at review produces no new request. The original encrypted session stays at r4.

An independent test assertion checks this narrower business authorization before accepting the fixture. It rejects unrelated requirement/interface changes, changed headers and altered navigation arguments. This adds no production semantic validator or repair authority.

## Technical structure and inference

[Measurements](composition-metrics.json) distinguish missing correctness work from optimization: the original has 13 tasks / 28 steps / 7 sets / 5 workflows / 61,010 YAML bytes; the corrected artifact has 32 tasks / 49 steps / 13 sets / 17 workflows / 216,800 bytes, plus two typed group declarations. This is a correctness correction, **not a claimed size reduction**. Compiler v15 and its output for unchanged plans are untouched.

Necessary technical tasks retain complete original pages and records, check per-page truncation, guard nullable fact IDs, strictly reconnect offered/original records, capture exact nullable values across item scopes, validate three cells and one-to-ten data rows before writing, and check writer success before publishing outputs. The existing explicit checked boundaries avoid the previously retained v15 pure-branch/direct-array limitations; this task does not change the compiler to hide those issues.

Eight inference declarations become five: bounded query-to-URL conversion, search extraction, product-ID decision, detail extraction and detail-ID decision. Typed copies, status predicates, ID reconnection, original-text copying and row assembly use zero inference. Local adapters prove expressibility and execution, not future model compliance.

## Execution and remaining limits

See [independent workbook findings](workbook-oracle.json), [local evidence](local/) and [validation results](validation.md). Real Flow, Browser and Document processes use loopback networking and deterministic inference only. The complete case visits ten products and writes eleven rows / 33 exact cells. Tests cover repeated IDs, one product, nullable URLs, missing facts, unoffered/unknown IDs, incomplete acquisition and denied writes. They inspect actual navigation arguments, exact descriptions, literal formula-looking strings, blank positions, writer paths and Browser closure.

The unchanged independent observed-product oracle accepts complete, one-product and repeated-selection cases and rejects rewritten text. Local fixture ground truth separately establishes legitimate blanks; the conservative live oracle still refuses to certify absence from observations alone. No live business acceptance is claimed.

Encrypted terminal recovery returns the same outputs and workbook bytes with zero repeated inference/navigation/writing. Existing receipt, ownership and uncertainty tests remain part of the required validation. All historical approvals and reservations are preserved. PR #117 remains draft.
