# Proposed exact auxiliary-listing amendment

**Inactive. Separate workflow/verifier activation approval required. No added budget.**

The source-only authorization `user-amazon-deals-source-only-20261113` has been used. Diagnostic01 completed 38 exact observed `https://www.amazon.fr/deals` GETs. Fresh combined diagnostic02 retained 1,580 source records and 57 own reads in original order: 38 exact-address occurrences and 19 previously authorized sponsored resolutions. Every bare `/deals` read returned HTTP 200 at that same exact address in one GET. The three own sponsored product reads, exact review alias and auxiliary offer link belong to the same retained source and empty-ASIN div context. No observation loss is demonstrated. V10/v14 correctly remains blocked under its current grant.

## Concrete proposed authority

Implement and activate a separate **policy v11 / business verifier v15**, preserving v10/v14 and all older versions. In a fresh complete owned search, allow only the literal fragment-free `https://www.amazon.fr/deals` already covered by the source-only experiment to participate in source proof. Use the owning Browser's existing observed-entry mechanism, expiring with its current source; never add an unconditional route or host. No queries, `/deals/`, fragment, rewritten URL or unobserved address is included. Every redirect is checked before contact against existing authorized routes or that same exact observed address.

Every exact-address occurrence, including repetitions, needs its own ordered complete capture. Only an unchanged single GET 200 to the same literal listing address can support this auxiliary classification. A product destination, redirect chain, different document URL, reused capture, missing occurrence, query or spelling variant cannot establish that proof.

Only in the observed **empty-ASIN div context without a semantic occurrence**, with exactly one heading and consistent original context, may a non-heading auxiliary anchor at that exact address be accounted for as listing navigation. All remaining card members must already have their own ordered product proofs for one consistent identity, or the previously authorized exact same-card `baseHref + #customerReviews` relationship. This adds no general non-product inference and cannot reclassify a heading, product card, unknown sibling or mixed context. Already identified products retain their existing source identity and rules; all their original records remain.

The workflow must retain the complete raw source, original references, repetitions, every product occurrence and order. Source-proof product reads remain explicitly separate from all later first-N business product visits. Names, descriptions, prices, actual Excel contents, cleanup, business reviews, execution approval and independent oracle remain mandatory. A new auxiliary proof creates no business visit or product omission. No SDK/network, model, provider, protocol, timeout, retry, output allocation, financial or TaskPlan-format authority changes.

## Reviewable implementation and validation

The new `AnalyzeAmazonDealsContextProposal` helper is offline only: absent from policy capture, Browser configuration, planning facts, entry grants and business-oracle dispatch. It uses existing complete-source, metadata, navigation and snapshot checks, retains raw records and reports relationships without qualifying a source or workflow. Its response always has `active`, `source_prerequisite_qualified`, `business_oracle_passed` and `navigation_authorized` false. It performs no navigation or inference.

The controlled tests retain v14's rejection and cover own ordered evidence, repetitions, both prior sponsored route families, an identified product before the unresolved card, cold equality, and negative permission/provenance/context/identity cases. The retained-source analysis and exact validation counts are linked in the accompanying evidence. They establish the proposed relationship only, not activation or full source qualification.

After explicit approval: implement/version the isolated policy and oracle amendment, run affected regressions, then require a **fresh real-profile source preflight** under that exact version before any paid full Amazon planning. Representative model delivery and actual execution/oracle remain separate gates. The old source Browser is closed; no stopped session will be resumed. The existing finite EUR1,700 campaign ceiling and all reservations remain.
