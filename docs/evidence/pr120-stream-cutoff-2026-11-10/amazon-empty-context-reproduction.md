# Retained empty-context reproduction

Source: the complete 1,495-record snapshot `ef6854e34bd4423999244dcb1bf617b1`
already captured with the real benchmark v3 profile. Its 65 `/clk/` links are
authorized route shapes; it contains no `/clk` link. No new Amazon acquisition,
generation, product visit or workbook run was performed in this follow-up.

The three individually complete retained DOM fragments are sufficient to
reproduce the observation defect. The truncated whole-page HTML is not treated
as complete. [Machine-readable findings](amazon-empty-context-findings.json)
bind each fragment to its UTF-8 digest and original snapshot-local context index.

| Empty `data-asin` context | Observed links with that nearest context | Nonempty descendant ASIN elements | Demonstrated missing observation |
| --- | ---: | ---: | --- |
| 0, outer collection div | 4 | 3 | Two image-only links have explicit accessible labels absent from `text` |
| 23, outer carousel div | 4 | 10 | Two image-only links have explicit accessible labels absent from `text` |
| 86, related-brand div | 9 | 0 | An explicit ARIA heading exists in the DOM but is not classified as a heading |

The empty attribute is actually present on all three outer divs. It was not lost
by ancestor-attribute extraction. Child product links retain their own nearest
nonempty attribute; that identity cannot be assigned to an outer collection link.
All 17 observed outer-context links have zero exact URL aliases among observed
links with a verified product identifier. No advertising target was decoded or
constructed. Static DOM counts include hidden carousel variants and must not be
interpreted as missing visible records without the original layout state.

## Demonstrated generic observation defect

The existing observer considered only `innerText` for links/headings and omitted
`[role="heading"]` from its semantic element classification. The
[minimal production-host regression](../../../tests/GnOuGo.Browser.Mcp.Tests/RetainedEmptyContextObservationTests.cs)
reduces the shared structures of the three retained fragments: an empty collection
wrapper with a labelled image-only link, a separately identified child product,
and an empty related-brand wrapper with an ARIA heading and ambiguous links.
Fixture URLs are explicitly synthetic; retained advertising URLs are neither
rewritten nor contacted. This is a local structural reproduction, not a replay
of the original page's scripts, CSS, layout or live source acquisition.

The regression failed on the original observer because the accessible label was
empty. After the generic fix it passes, retaining the ARIA heading too. Assertions
preserve every fixture link, order, case-sensitive opaque URL bytes and nearest
ancestor value, reject borrowing a child identity, and keep hidden content out.
The complete Browser suite passes 181 tests; 98 existing source/route/oracle
regressions also pass, including ambiguous empty attributes, sponsored omission,
wrong order, substituted destinations, `/clk`, and external hops.

The correction restores observation data. An accessible brand label, nested child
product, or heading elsewhere in the wrapper still does not prove whether the
outer advertising destination represents a product. No ambiguous card is filtered
and v7 still cannot qualify this retained source.

## Original obligations and verifier boundary

The original `AmazonPrompt` and `AmazonEvaluationPrompt` require the first products
(at most three), actual visits, name/description/price in the saved workbook, a
required `query` input and cleanup. They do not require a ten-character ASIN in
the search DOM or a navigation-role wrapper. v7's source-ASIN condition and its
sole empty-context exclusion (`nav`/`navigation`, without product occurrence or
heading) are additional proof conditions. The current captured v3 policy also
explicitly retains this exclusion boundary. This observation grants no authority
to weaken that captured policy or to reinterpret an empty div as a non-product.

A possible **v8 design proposal, not implemented or activated**, could prove a
product occurrence through a source-bound semantic item and its exact primary
link, then verify destination identity after the already authorized GET. It would
need a separately versioned proof contract and preserved negative tests for unknown
wrappers, mixed identities, duplicate occurrences, incomplete sources, wrong order,
unapproved routes and missing redirects. It would not require a source ASIN where
equivalent product provenance is independently demonstrated. The original business
result requirements and current route grants would remain mandatory.

That proposal does **not** resolve these outer divs: none has a proven semantic
product occurrence or exact link alias to an identified child. Excluding them
by empty identifier, brand text, or collection shape would be an unsupported
omission. Any policy change requiring new authority remains a proposal for explicit
agreement, separate from the budget; no new permission is inferred here.

Resume a complete Amazon qualification only after a source-only check on the actual
profile preserves every candidate and establishes its product/non-product provenance
under an approved versioned proof contract. Run no paid Amazon planning merely
because accessible labels now survive capture.
