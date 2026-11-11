# Source-only outcome and remaining proof boundary

The authorized run `amazon-source-resolution-20261111-01` completed from repository
HEAD `614c0aad23a70f50d7dbe83e30758a2e2213ae62`, using the frozen benchmark/Browser
Release build of `0328d1b1`. The runner, actual host policy and binary fingerprints
are captured in [result.json](result.json); private artifacts retain the literal
requests, responses, complete observations and issue records.

The fresh search has **1,564 complete records** and **17 unresolved sponsored
links**. Every exact occurrence was requested once in order, without deduplication
or decoding its advertising target. All 17 returned observed `302 → 200` chains
to approved `/stores/page/{UUID}` destinations, covering five distinct store-page
paths. There are 35 recorded navigation hops including the search; this is not a
count of incidental browser resource requests. Every terminal observation is
complete. The browser closed and a subsequent read reported no active page.

A separate process reloaded the saved events and confirmed complete resolution
with `amazon-source-resolution-v8-proposal`. No model call, product extraction,
workbook, full workflow or business-oracle pass occurred. The campaign audits
before and after are deeply identical: ceiling **€1,700**, remainder
**€416.028858055317264736945128318**, and every cost/reservation/history retained.

## Demonstrated cause and inactive proof delta

No ASIN was lost by observation. These exact empty-ASIN links actually lead to
store navigation. V7 only recognizes its narrower source `nav`/`navigation`
evidence and therefore still reports `search_product_context_unverified` and
zero eligible entries. Its product-visit findings are also expected: diagnostic
store reads are not the required product visits.

The additional, **inactive** `amazon-resolved-store-context-v1-proposal` reuses
V7's context/occurrence checks with one explicit independent-evidence input.
It does not edit, filter or annotate the retained source. It permits a source
link to be accounted for as store navigation only when:

1. Every unresolved sponsored source occurrence has its complete ordered exact
   redirect proof under the separately approved source-only route scope.
2. This exact record's independently observed destination is an approved store
   page. Brand text, decoded parameters, child ASINs or a different record's
   destination supply no proof.
3. Its observed nearest context is structurally valid and consistent, its ASIN
   is empty, and it has no product heading, semantic product occurrence or direct
   product URL. A product destination or conflicting product evidence remains a
   blocker; nothing is silently converted into navigation.
4. All remaining original identity, alias, source-completeness and occurrence-order
   checks pass. Repeated product identities in distinct occurrences remain distinct.

On this real retained source the proposal accounts for all 17 store links and
validates **82 product occurrences**, with first products `B09897279N`,
`B0HDC2RK3T`, `B0H1CCV6ZG`, in source order. See the sanitized
[offline result](context-proposal-result.json). It explicitly retains
`active=false`, `source_prerequisite_qualified=false` and
`business_oracle_passed=false`. It is absent from policy capture, live redirect-entry
authority and business-verifier dispatch. Original V7 calls pass no new evidence
argument and preserve their behavior. **172 Amazon regressions pass**, including
all 161 previous cases plus 11 controlled proof/negative cases.

The source-only approval has been fully used for diagnosis; it does not authorize
activating this proof rule in a workflow. Resuming full Amazon qualification needs
a separately approved, versioned workflow policy and business-verifier amendment:
use this exact evidence rule for non-product navigation and permit its same narrow
source-resolution route scope within a fresh workflow. Product reads, order,
duplicates, fields, Excel output, cleanup and independent execution verification
must all remain mandatory. The closed diagnostic browser supplies no live authority
to another session, and its results cannot substitute for a new workflow's source.

## Controlled correction and validation

The generic Browser route correction in `0328d1b1` was demonstrated with three
failing tests before implementation, then all **186 Browser tests passed**.
The literal absolute URL and transmitted URI must match the same allowed rule;
canonicalization cannot silently remove a forbidden explicit port/path spelling.
Absolute redirect validation occurs before contact. An omitted HTTP root path
continues to mean `/`. No Amazon rule was added to Browser or the planning engine.

The shared non-Server run passed **6,701 tests with 12 existing skips**. The final
Server partition run, Release build and CI are recorded subsequently in workspace
artifacts and PR checks; this document does not predict their result.

Code remains blocked on unavailable route-owner access, as the user confirmed.
Its packet is ready and unsent; no new inference or stopped-session replay occurred.
