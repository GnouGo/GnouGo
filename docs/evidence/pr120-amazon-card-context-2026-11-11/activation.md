# Authorized v7/v11 activation

The user explicitly approved the exact [proposal](proposal.md) from commit
`7969d62b87477f56eb8bd9372ca470f8da3e6596`: **“Authorize exact v7/v11 amendment.”**
This adds no budget or navigation permission. The existing finite campaign,
historical costs, uncertain reservations and stopped-session barriers remain.

The benchmark now captures policy `amazon-observed-card-review-context-v7`,
grant `user-amazon-semantic-card-review-20261111` and independent business oracle
`amazon-business-v11` only through the explicit new CLI selection. The navigation
pattern delegates directly to v6. Existing versions remain separately selectable.
No production engine, provider, transport, timeout, retry or allocation changes.

The demonstrated source-context gap was in the deliberately narrower verifier,
not missing native DOM observation. The new branch uses original semantic card
boundaries and ASIN metadata. Every navigable unresolved occurrence keeps its own
ordered GET; exact review aliases refer only to completed proofs in their card.
Known DOM-ASIN auxiliary reviews use separately labelled source-card membership,
without claiming HTTP equality or a redirect receipt. Raw records and URLs are
never rewritten. Later ordered business visits and the full workbook/execution
oracle remain mandatory.

Controlled regressions cover three child cards under one empty ancestor, six own
redirect reads followed by three separate business visits, repeated hrefs and
identities, known cards without an exact HTTP twin or heading, cold reload and
unchanged raw source. Negative cases cover missing/reordered proof, wrong product,
changed query, heading/self-occurrence aliases, cross-card borrowing, inconsistent
metadata, unresolved siblings, failed/incomplete evidence, expiry, missing visits,
and workbook/result failures. The original v10 already accepts some known-ASIN
`/sspa/click` reviews as source data; a regression preserves that legacy behavior.

The red baseline failed four new checks before activation. With the implementation,
314 Amazon regressions pass, and the broader affected set passes 1,149 tests with
`-warnaserror` (zero skipped in either run). These sets overlap. Private full logs
are retained under `artifacts/pr120-v11-20261111/`; they contain no new model calls.

This commit establishes implementation and controlled validation only. Actual
three-card redirect proof, fresh real-profile source qualification, an accepted
new plan, execution and independent-oracle qualification remain separate gates.
The next experiment is one fresh v7/v11 source-only preflight on the frozen build,
with actual exact-next authority, complete observations, durable events, cold
oracle comparison and verified Browser cleanup. No paid workflow may precede it.
