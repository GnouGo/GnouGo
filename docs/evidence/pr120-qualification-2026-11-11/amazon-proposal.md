# Amazon provenance: retained source and inactive resolution proposal

The actual-profile source `5b8184daa25741bc987b67a7dc77a03d` has 1,527
complete records, 44 `/clk/` links and no `/clk` link. This investigation adds
no source acquisition, product visit, generation or workbook execution.
The [17-link manifest](amazon-link-provenance.json) binds each source record to
one exact anchor in a complete retained DOM context and hashes its unchanged URL.

| Records | Placement evidence | Independent product relationship |
| --- | --- | --- |
| 58, 59, 60, 63 | `sb-themed-collection-desktop` metrics/painter; headline and CTA roles | Three separately identified descendant products; no exact href alias |
| 394, 395, 396, 401 | `sb-themed-collection-v2-desktop` metrics/painter; headline and CTA roles | Three separately identified descendant products; no exact href alias |
| 1213–1221 | `multi-brand-creative-desktop` in the brand-footer slot; related-brand ARIA heading | No identified descendant product and no exact href alias |

All 17 nearest `data-asin` values are actually empty in the retained DOM. No
identity was lost by observation. The existing accessible-label/ARIA correction
already preserves the available labels. The eight collection links and nine
brand-navigation placement links each advertise a `/stores/page/{uuid}` target
in `r`. That is an **untrusted source hint, not an observed redirect**. The analysis
neither navigates to the decoded value nor treats it as a product identity or
permission to exclude a link. Empty ASIN, brand text, placement markers and child
identities alone remain insufficient. Every link stays unresolved under v3/v7.

## Original obligation versus v7 proof technique

Authority is `LiveWorkflowEvaluation.AmazonPrompt`, its unchanged evaluation
constraints (at most the first three products for this campaign), and the separately
captured navigation grants. The original obligation is about products and their
order, not about every advertisement being a product.

| v7 condition / finding family | Original obligation | Proof technique / authority boundary |
| --- | --- | --- |
| Known oracle version, exact v3 policy/grant and product bound 1–10 | Preserve captured requirements and permissions | Version/profile binding; the campaign requests three |
| Successful whole-document search, requested/actual `/s` query equals input, no fallback, complete pages/cursors/counts | Search the supplied article; do not hide earlier results | Snapshot and whole-root evidence; not a requirement for a particular HTML layout |
| Exact snapshot-local record reference, consistent context index/value/tag/role | Preserve source provenance and order | The observed nearest-ancestor representation is a proof technique |
| Nonempty ten-character source ASIN, matching direct-product identity | Visit the actual listed product | Source ASIN is one identity proof, not an original requirement; an equivalent proof needs its own version |
| Empty ASIN excluded only for observed `nav`/`navigation`, without heading/occurrence | Do not substitute products or omit sponsored products | This narrow exclusion is explicitly captured in v3; collection labels do not extend it |
| Well-formed semantic occurrence, consistent selector/index, strictly increasing new occurrence order | Preserve occurrences/order, including repeated identifiers | Semantic-item/context grouping is a proof technique; equal ASINs alone cannot deduplicate |
| Primary heading links or all links when no heading; one consistent identity; at least one approved exact URL | Select the intended product rather than a variant or unrelated card link | Heading preference and alias checks are proof techniques; ambiguity remains failure |
| All product/source routes checked; every hop explicit GET, allowed exact route, bounded acyclic chain, observed 2xx final | Use only granted navigation; do not invent a destination | Mandatory security/permission boundary, independent of business proof |
| First N products visited after search, exact source URL membership, destination identity and count/order match | Visit each first product in order; no later substitution | Actual source-to-destination proof; never a constructed `/dp` URL or decoded advertising target |
| Successful workflow; file exists; header/name/description/price columns; product-bound row matrix | Save the requested Excel file on disk | Independent workbook inspection and successful writer receipt |
| Complete product observations supporting each row, product identities/order, explicit unavailable values | Extract truthful name, description and price from the visited pages | Observed field/price evidence; generated text alone cannot establish extraction |
| Browser has no active page after workflow, plus cleanup even on failure | Close the browser as captured | Independent closure probe; later cleanup by the oracle is not workflow success |

No demonstrated generic observation defect remains in these 17 identity fields.
The current `search_product_context_unverified` is a proof/policy boundary, not
an output-token setting. V7's `first_product_visits_incomplete` is also expected
for this source-only evidence; no visits took place.

## Concrete, inactive proposal

`LiveWorkflowOracles.AmazonSourceResolutionProposal.cs` provides the separately
versioned **`amazon-source-resolution-v8-proposal`** offline verifier. It is not
registered in policy capture, Browser allowlists, redirect-entry authorization or
the business-oracle selector. The existing v7 path keeps its original checks.
Only the existing redirect-evidence routine is shared through a private route
predicate; no production engine or public contract changes.

The proposal consumes a complete successful search followed by exact source-link
GET evidence, one for every sponsored link lacking a verified source identity, in
source order. It requires complete observations and the full redirect chain. It
classifies an actually observed product destination as a product and an actually
observed `/stores/page/{uuid}` destination as store navigation. It never consumes
`r`, a brand label or a child ASIN as proof. Missing, omitted, reordered, substituted,
expired, contradictory or unauthorized evidence fails. Equal destination ASINs do
not merge occurrences.

Even a controlled pass returns `source_prerequisite_qualified=false` and
`business_oracle_passed=false`: destination resolution is not occurrence/alias
verification, an activated source policy, or a complete workflow. On the retained
source it must still report all 17 unresolved destinations because the necessary
redirect observations do not exist. No v8 business verifier has been activated.

## Exact next authorization, separate from funding

Proposed scope is **source-only diagnosis**, not full Amazon planning:

1. A fresh successful complete Amazon.fr search creates transient authority to GET
   each exact sponsored link whose source product context is unresolved. This
   diagnosis may run before v7's all-contexts-qualified gate. It may not construct
   or substitute a URL. Source authority expires on close or a new search.
2. Existing approved routes remain unchanged; add only redirect destinations
   matching `https://(?:www\.)?amazon\.fr/stores/page/{UUID}` with an optional
   nonempty query. UUID is exactly 8-4-4-4-12 hexadecimal digits. No new hosts,
   credentials, explicit ports, fragments, `/clk` variant, methods or general
   `/stores/*` permission. Check every hop **before contact**, including this new
   route. A decoded `r` value is never directly requested.
3. Retain every link and every response. Product destinations remain products;
   ambiguous destinations remain blockers. Close the browser. No model call,
   workflow execution, product omission or weakening/activation of v7 follows.

This needs the user's explicit navigation approval under the current request and
captured v3 policy. It has **not** been performed. The offline proposal makes this
scope reviewable; activating a source-only host profile is subsequent work after
approval. A later v8 business-proof change would require a separate concrete delta
based on the observed redirects and occurrence evidence, before full Amazon planning.
