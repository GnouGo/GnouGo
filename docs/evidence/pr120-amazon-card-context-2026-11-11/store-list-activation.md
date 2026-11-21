# Authorized inherited store-list amendment

The user explicitly replied **“I approve”** to the exact [proposal](store-list-proposal.md)
at commit `d13f699fddc9c37ca873b4d7c7e546634519bb8d`. This separate amendment adds
no budget or navigation permission and does not change the v7/v11 grant.

The benchmark captures `amazon-observed-store-list-context-v8`, approval
`user-amazon-inherited-store-list-20261111` and independent oracle
`amazon-business-v12` only through its explicit new CLI option. The full navigation
pattern delegates to v7/v6 without changes. Prior policy and oracle behavior remains
separate. No production engine, provider, model, timeout, retry or allocation changes.

The demonstrated condition is the original eight-link/two-list-item source shape
in [the retained actual-profile result](fresh-v11-source.md). Each link has an own
checked store receipt, but v7/v11 preserves its no-occurrence store condition. V12
uses a separate evidence input only for validated inherited `li` / `listitem` store
cards. All original links need own ordered store proofs, consistent empty-ASIN and
semantic metadata, and an auxiliary anchor distinct from its inherited container.
Headings, direct product identities, mixed cards, self-occurrences, unresolved
siblings or inconsistent metadata remain blockers. Classification evidence points
to existing physical events and expressly claims no new call, product or business
visit. No raw source is removed or rewritten.

Controlled coverage combines three product cards and six own product proofs with
eight store proofs in two inherited list items, followed by three separate business
visits. It preserves duplicate URLs as separate reads and cold/raw equivalence.
Negative tests cover missing/reordered receipts, unresolved siblings, headings,
self-occurrences, own semantic roles, unsupported containers, inconsistent metadata,
mixed product destinations, direct product links, failures, truncation, expiry and
missing business visits. The native XLSX oracle retains its positive case and all
11 workbook/field/visit/execution/cleanup negatives.

Before implementation, three functional checks failed against the v11 behavior
(source qualification, exact grant and ordered authority). A fourth red result was
a fixture error in the mixed-destination case; it was corrected separately and is
not counted as a demonstrated implementation defect. Private logs retain both.
The complete affected Release set passes **1,182 tests**, zero skipped, with
`-warnaserror`; the private log is `artifacts/pr120-v12-20261111/affected.log`.

Implementation and controlled checks do not qualify a live workflow. A fresh
actual-profile source prerequisite remains mandatory before paid planning. The
closed v11 source supports offline comparison only; its receipts confer no authority
on another browser. Full qualification still requires artifact-bound review, actual
ordered product visits and extraction, a real workbook, cleanup and the independent
business oracle, twice consecutively on one frozen version. All existing campaign
costs, uncertain reservations and stopped-session barriers remain unchanged.
