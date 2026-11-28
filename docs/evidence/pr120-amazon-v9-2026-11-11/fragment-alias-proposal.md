# Proposed exact review-fragment proof alias — inactive

The first v9 preflight observed 1,548 complete records. In one empty-ASIN context,
records 396, 398 and 403 contain literal navigable `/clk?…` URLs. Record 401 has no
heading or semantic occurrence and contains **exactly** the href of records 398
and 403 followed by `#customerReviews`. All four retain the same original context.
V9 correctly rejects the fragment route before any dependent GET. Nothing was
normalized, navigated, omitted or accepted; the browser closed successfully.

The [read-only source facts](fragment-alias-facts.json) compare original strings as
`alias.href == separatelyObserved.href + "#customerReviews"`. They do not construct
or return a navigation target, infer a product identity, reuse a receipt or qualify
source/business completion. Controlled checks reject a changed query, another
context, a heading-bearing alias, another fragment and percent-encoded substitutes.

## Exact proposed verifier-only amendment

Under a new explicit policy/oracle version, an empty-ASIN product context could
account for this exact non-heading review link as a **proof alias**, only when:

- Its original href is exactly a separately observed permitted source href plus
  the literal `#customerReviews`; no URI normalization, decoding, query changes or
  constructed target can establish equality.
- The original context agrees and the existing one-heading/no-conflict checks pass.
  It is not another product context or semantic product occurrence.
- Every navigable source-link occurrence still has its own complete ordered GET
  proof, including repeated equal hrefs. All relevant HTTP/document identities
  agree. The alias receives only a reference to that verified evidence; it is
  never represented as another HTTP call, independent receipt or product visit.
- The original alias record, source order and every product occurrence remain in
  the retained evidence. Distinct contexts never merge because IDs or hrefs match.
  Missing/mismatched proof still fails. Store-navigation proof cannot supply the
  product identity for this exception.

**No fragment URL would be navigated.** Full-URL navigation patterns, hosts,
permissions, original hrefs and the already approved two-phase visit order stay
unchanged. Required business visits, fields, workbook, writer receipt, successful
execution, cleanup and the independent oracle remain mandatory. V5/v9 and earlier
versions keep their current refusal; the budget and historical journals do not change.

This requires separate verifier approval because the previous amendment explicitly
required each source link's own redirect proof. The proposal substitutes a precisely
bound same-HTTP-resource proof reference only for this observed review fragment.
It is not authority to strip fragments, ignore arbitrary links or omit a product.
