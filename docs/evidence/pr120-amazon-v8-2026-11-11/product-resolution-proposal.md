# Proposed product-resolution workflow amendment — inactive

## Established evidence

The authorized source-only diagnostic independently followed three exact `/clk?…`
links to product `B0H8RD31XM`, with complete `302 → 200` GET chains. The links share
one observed nearest empty-ASIN container (index 28), have one heading-bearing
link, and account for every observed link in that container. Their source records
and destinations remain unchanged. The read-only association checks reject
missing links, different destinations, mixed containers, a second heading,
borrowed source identities and unresolved links in the container.

The [retained result](slashless-diagnostic/live-result.json) records destination
facts, not an accepted occurrence count. Empty ASIN alone and equal destination
alone remain insufficient. The additional container-marker diagnostic was
inconclusive: its fresh page had no slashless links. It supplies no missing marker
for the earlier source. The next ordinary v4 preflight again saw forbidden `/clk`
links and failed without any dependent read; no further equivalent retry is planned.

## Exact proposed activation delta

A separately versioned policy and oracle would preserve v4/v8 and add:

1. Literal `https://sponsored-ads.amazon.fr/clk?…` entries from the current complete
   successful search, without normalizing `/clk` or constructing/decoding a URL.
   The host and every other permitted route stay unchanged. Every hop remains
   checked before contact; close, failure or a new search expires source authority.
2. An independent identity-proof alternative for an empty-ASIN product context:
   every exact sponsored-link occurrence in that context must have its own complete
   ordered resolution; all HTTP and document destinations must identify the same
   product; the original context metadata must agree; exactly one source link must
   carry the observed heading; no source identity, semantic occurrence, direct link
   or other unresolved link may contradict that association. Otherwise fail.
   This would reuse the original physical-context grouping, supplying identity
   from the separate evidence map rather than rewriting `data-asin`. Different
   contexts remain different occurrences even when their destination IDs match.
3. An **explicit source-resolution phase before business product selection**.
   Some of these reads now reach actual products. This is a real workflow boundary
   change, not the existing store-navigation exemption. Keep and report every early
   product read separately; never relabel it as a store or count it as a required
   business visit. After the complete source qualifies, still perform every
   required first-N product visit in original source order, using exact source
   links, and verify identity, fields, Excel, writer receipt, execution and cleanup.
   Missing, duplicated or reordered required business visits must still fail.

The third point needs explicit agreement because v8's source-resolution exemption
covers only proved non-product stores. It cannot silently exempt actual product
reads from the current visit-order oracle. No current run gains this authority.

No model, provider, transport, allocation, budget, TaskPlan architecture, contract
or original required product/output set changes. All source evidence, products,
occurrences and financial holds remain. No product is dropped to obtain a pass.
The first later live would require the new actual-profile source prerequisite;
two full execution-plus-independent-oracle successes would still be required.

## Status

The association facts and 48 controlled diagnostic checks are available. This
workflow rule, two-phase ordering and full-workflow `/clk` route are **not active**.
Implementing/qualifying the separately versioned amendment needs the explicit
approval above; the source-only grant and existing budget do not imply it.
