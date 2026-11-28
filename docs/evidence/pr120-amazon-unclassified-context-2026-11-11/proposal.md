# Source-only AAX investigation requiring separate navigation approval

The retained complete source `amazon-v12-source-20261111-04` contains 1,340
records. Four original anchors at ordinals **316, 318, 321, 323** share the
unchanged empty `data-asin` ancestor (div, index 16), with no semantic child
occurrence. Record 318 has a heading. Their host is
**aax-eu-zaz.amazon.fr**, with literal path prefix **/x/c/**. Record 321 has
`#customerReviews`; records 318 and 323 have identical literal hrefs.

The browser retained the heading, hrefs, selectors, references and original empty
attribute. This is not demonstrated observation loss. The page provides no
verified identity in that context. A product-like heading and an embedded target
string cannot establish a product destination or classify the context as
non-product. No target is decoded or navigated during this investigation.

V12 reports its source prerequisite satisfied after the 23 permitted source
resolutions, but its candidate predicate excludes this other host. That is a
coverage gap, not permission to discard the four records. The inactive generic
coverage analysis exposes heading-bearing contexts excluded by the existing
route/identity predicates, retains every original member and repetition, and
grants no identity, navigation or business credit. A sanitized controlled case
preserves V12's historical favorable verdict while exposing the missing context;
cold analysis is identical and input bytes remain unchanged. All 76 affected
Amazon tests pass with `-warnaserror`. Existing versioned oracles remain intact.
Full source coverage and a complete Amazon qualification are **not established**.

## Proposed narrow diagnostic

Authorize a **fresh source-only** investigation, with no model calls or full
workflow. After one successful complete Amazon.fr search on the actual profile:

- GET only exact original, fragment-free
  `https://aax-eu-zaz.amazon.fr/x/c/...` links observed in its unresolved context,
  in source order, preserving repeated href occurrences and their own receipts.
- Check each redirect before contact against the existing approved Amazon.fr
  search/product/store routes or an exact original observed AAX entry above.
  Other hosts, ports, credentials, schemes, paths, fragments and unobserved AAX
  targets remain forbidden. Do not use an embedded or decoded target as authority.
- Preserve every raw record, heading, ASIN context and alias. Never navigate or
  strip the review fragment; its possible exact-twin relationship is analysis
  only, with no classification or verifier activation granted here.
- Close the browser and expire source authority on close, new search or failed
  evidence. Persist original occurrences, ordered hops, destinations, incomplete
  reads, cleanup and cold analysis separately from later business visits.

This proposal adds **one new host and one path prefix**, solely to that diagnostic.
Existing grants do not cover that host. It adds no funding and does not authorize
new product classification, a workflow policy/oracle version, product omission,
URL normalization or a paid full Amazon plan. Any later activation is a separate
reviewable amendment based on the actual destination evidence.

No AAX GET has been performed. Until approval and destination proof, no new full
Amazon plan is funded. Local planning and accounting investigation can continue.
