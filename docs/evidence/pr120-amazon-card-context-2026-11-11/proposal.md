# Proposed card-context review accounting — inactive

The fresh v6/v10 preflight [fails before dependent reads](fresh-source-refusal.json).
It retains 1,431 complete records and 24 sponsored `#customerReviews` links:
three in distinct semantic child cards under one empty-ASIN ancestor, one in the
already supported empty-ASIN shape, and 20 in nonempty-ASIN source cards. Three
of the latter have no separately observed identical HTTP prefix. All records and
their original hrefs remain retained. No fragment was navigated or stripped.

This demonstrates no observation loss: the native Browser already reports the
nearest semantic occurrence and ASIN ancestor separately. An inherited occurrence
describes a parent container; it does not make every descendant anchor another
product card. The existing v6 exception deliberately requires no occurrence and an
empty-ASIN exact sibling relationship, so it cannot authorize these additional cases.

## Concrete proposed policy v7 / business verifier v11

This requires a separate explicit business-verifier/workflow amendment, not budget
authority. Navigation patterns and hosts would be **identical to v6**. V6/v10 and
earlier versions would retain their existing refusal. No rule enters the engine.

1. **Empty-ASIN cards with observed semantic boundaries.** Use the original native
   semantic container to distinguish child product cards inside an empty-ASIN
   ancestor. Require complete, consistent container selectors/ordinals and ASIN
   metadata, original record references/order, and exactly one heading in each
   resolved product card. Every navigable unresolved sponsored occurrence still
   needs its own complete ordered GET and matching product redirect evidence,
   including repeated equal hrefs. All identities within that card must agree;
   missing/contradictory evidence or unresolved siblings remain blockers. No raw
   attribute, selector, reference or URL is rewritten to manufacture a context.
2. **Inherited review anchors in those cards.** A non-heading anchor that is not
   itself the semantic product occurrence may reference completed product evidence
   only for separately observed permitted hrefs in that same card where
   `review.href == observed.href + "#customerReviews"` exactly. Preserve every
   navigable occurrence's own GET; the review receives no extra call or receipt.
3. **Already identified nonempty-ASIN cards.** Reuse the existing known-product
   context validation, including consistent observed ASIN and semantic provenance,
   no contradictory direct product identity, and an independently observed permitted
   product entry in that same card. A non-heading auxiliary review anchor may be
   accounted for by membership in that validated source card. **This branch would
   not require an exact HTTP-prefix twin.** It claims neither HTTP-resource equality
   nor a reused redirect receipt; the identity basis is the existing observed DOM
   ASIN context. Known-source selection already permits a card without an h2 when
   its context is consistent; do not invent a new one-heading condition for it.
   Standalone/self-occurrence anchors, cross-card borrowing, conflicting identities
   and missing permitted source entries remain rejected.

Every raw record and product occurrence stays in the complete source. Preserve all
cards and their original order, including repeated identities. No ambiguous card
is omitted. First-N business product visits still use original permitted observed
entries, in source order, after complete source qualification; every visit retains
its own observed identity and fields. Extraction, actual Excel with headers and all
rows, writer receipt, execution, cleanup and the independent oracle stay mandatory.
No `#customerReviews` URL or derived stripped/decoded target becomes navigable.
Unsupported review links or missing proof still block the entire prerequisite.

## Work completed before requesting activation

The [inactive relationship inspector](proposed-relations/ReviewCardFacts.cs) returns
only original record references and proposed relationships. It never returns a
navigation target, qualified source, business pass or HTTP receipt. It is not a v11
verifier and is not registered in any capture, redirect guard or oracle selector.
[Read-only results](read-only-card-relations.json) account structurally for the 24
records under these proposed conditions; cold results are identical and source
bytes remain unchanged. The 36 controlled checks include zero/one/multiple cards,
repeated hrefs and identities, changed queries in the empty-card branch, different
cards, headings, self-occurrences, nonsemantic containers, missing entries,
unknown/conflicting identities and inconsistent metadata. Build uses `-warnaserror`.

The original v10 still rejects the source. Actual redirect proof for the newly
observed three-card empty-ASIN shape is **not established**. The separately
[authorized source-only diagnostic](authorized-diagnostic-target-absent.json)
used one fresh search to test that hypothesis; its page had no slashless links, so
it issued no sponsored reads, verified cleanup and stopped without another search.
It does not prove the proposed source qualification or full workflow.

If authorized, implement the separately gated v7/v11 source-context rules and full
negative regressions, then validate them on a fresh actual-profile source with
every required own redirect read before another paid full workflow. Preserve the
single TaskPlan, scoped repair, business reviews, frozen-run success criterion and
all financial history. No existing stopped session is continued. No money is added.
