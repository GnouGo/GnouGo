# Retained Designer composition, corrected offline

Baseline: `a1c9d9a3`. Retained session `58975de9fd0e427a893d076dc5800862`, revision 4. This correction changes fixtures and execution tests only: compiler v15, production validation, repair authority, permissions, limits and public interfaces are unchanged.

## Artifacts and scope

- [Original/corrected generic fixtures](../../../tests/GnOuGo.Agent.Server.Tests/Fixtures/DesignerComposition/): task names and operation addresses are parameterized; original accepted requirements and literal operation arguments remain available for comparison.
- [Corrected TaskPlan](corrected-taskplan.json), [complete proposal](corrected-proposal.json) and [actual compiled v15 YAML](corrected-v15.yaml) use the retained session's task identities and operation contracts.
- [Machine-checked business diff](business-diff.json): all three previous requirements and public input/output contracts are unchanged. `cleanup_browser` is the sole added requirement. The five existing external operations keep their order and literal arguments. Only the search-control reference and writer row bindings change; the product visit moves into the available-URL alternative. Browser closure is added in `root.always`.
- [Historical hashes](historical-hashes.json) bind all five original revisions, both requests and both receipts read through public encrypted KeyVault APIs. No record was advanced, overwritten, archived or reconciled. No financial state changed.

The generated YAML is an **unapproved offline artifact**, not an update to the Designer session. Applying a paid revision to that session or executing this artifact still needs separate authorization and artifact approval.

## Corrected behavior

`each.output` now matches `candidatesPerPage`. Every complete page is validated and mapped independently; explicit one-level flattening supplies compact decisions. Original page records and producer completeness flags remain separate.

Selection returns offered identities only. Strict offered-candidate lookup runs before strict original-record lookup. The URL conditional visits the original URL when present; otherwise it returns a non-null record with permitted null facts and an explicit not-visited status. An actual acquisition failure remains a failure. Complete descriptive blocks are selected by original ID, then copied verbatim through lookup and typed projections.

Home, search, product and writer status checks use actual producer fields. `requires` continues to fail before dependent work; it never means “skip”. The original opposite sequential assertions are reproduced separately for both null and present URLs.

Rows are assembled deterministically as `flatten([[headerRow], productRows])`. The original public `rows` contract requires 1–11 rows, each with exactly four nullable-string cells. The writer and public exports receive the same checked collection. Headers are exactly `Nom`, `Description`, `Prix`, `Statut`; both returned paths come from the successful writer.

## Disposable revision evidence

[Exact saved-state replay](replay-result.json) starts from the saved r4 session with its two already committed calls. Both original response payloads validate against their original issued schemas. One explicit structural `revise` uses the current revision/hash, `preserveRequirements: false` and no `EditablePaths`, because cleanup is an authorized requirement addition.

The old discovery pages did not include `browser_close`. Two deterministic responses use existing discovery: query that capability, then inspect its contract. A third deterministic response supplies the corrected proposal. The disposable copy reaches review at r8 with five cumulative calls and zero repairs; the eight-call ceiling and previous accounting remain intact. No provider or MCP operation is called. Restart at review requests no further response. The retained store stays at r4.

Structural revision is deliberately capable of changing requirements. The regression's independent business-scope oracle checks the narrower authorization before acceptance, rejecting removed requirements, changed public contracts, changed query bindings and dropped writing. This is not a new production authority or semantic-proof mechanism.

## Necessary technical structure

The invalid retained plan has 16 tasks and nine inference transforms. The valid corrected composition has 39 root/nested tasks (plus two existing typed groups), six transform declarations, 62 compiled steps, 17 sets and 23 workflows; retained-identity YAML is 283,962 bytes. There is no valid original YAML or runtime baseline to compare against. No reduction claim is made against an invalid plan.

| Technical region | Independent responsibility |
| --- | --- |
| `home_records`, `result_records`, `product_records`, and their checked-page bodies | Check producer success, snapshot availability and both truncation flags; check each page before extraction while retaining authoritative originals. |
| `fill_control` and `check_submission` | Validate offered/original identity, obtain the scalar original reference from strict lookup, and assert both fill-success fields before observing results. |
| `detail_*` nullable branches and bounded projections | Guard nullable IDs, validate offered membership before original membership, copy the exact text, and construct one non-null record without array indexing or learned copying. |
| `rows`, `assembled_cells`, `check_cells`, `cell_contract` | Preserve observed order and the ten-product ceiling; validate exactly four nullable-string cells. The assembly boundary is needed by the unchanged v15 group-argument validation. |
| `checked_header` | The supplied header is used and checked by the same four-cell contract; it is not an unused forwarding task. |
| `checked_rows`, `workbook_contract` | Enforce the accepted 1–11 row contract before writing. Flatten alone does not prove these bounds. |
| `checked_writer` | Fail on an unsuccessful producer result before publishing the deterministic completion status. |

No inference copies typed values, reconnects IDs, checks producer status or formats/assembles rows. Remaining inference is extraction of observed candidate facts and genuine ID selection (search control, products and detail blocks).

## Local execution and limitations

Real Flow, Browser MCP and Document MCP run against a loopback-only site with deterministic inference adapters. Tests inspect actual navigation arguments, visits, XLSX headers/cells/order, blank positions, formula-looking literal strings, writer paths and Browser closure. Complete and repeated selections also pass the unchanged independent observed-product oracle; rewritten cells are rejected.

Missing-field, missing-URL and empty-selection fixtures have independently known local ground truth. Exact cells/blanks and header-only assembly are checked against it. The unchanged live oracle conservatively rejects unsupported absence claims and zero-product business outcomes; those cases are **not** reported as live business acceptance.

Encrypted restart reuses the committed terminal result without repeating inference, navigation or writing. Existing focused recovery tests cover retained receipts, stale submissions, cumulative allowances, tenant ownership and unknown-completion replay protection.

Two existing v15 limitations encountered during fixture construction remain outside this correction: a pure nullable branch with duplicate checked record aliases can fail with a duplicate contract key, and direct dynamic array arguments to a cardinality-constrained group can fail static graph validation. [Branch reproduction](deferred-pure-branch.json) and [failed-attempt evidence](failed-attempts/manifest.json) are retained. The delivered composition uses existing explicit validation boundaries; no compiler change or schema weakening conceals those limitations. Failed harness experiments, including an overbroad identifier rename in the disposable replay and the incomplete-snapshot mapping-count assertion, are retained separately from production findings.

## Validation status

Focused validation: seven new composition/revision tests, 37 existing Planning checks and 34 host/recovery/strict-lookup checks pass with `-warnaserror`. All eight new local execution cases pass. Full serial solution and candidate CI completion are reported in the [validation evidence](validation.md) and PR report. A passing deterministic adapter does not establish future model compliance. No paid planning or live execution is part of this work.
