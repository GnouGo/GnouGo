# Source diagnostic: document address and HTTP response are distinct

`amazon-aax-source-20261111-01` at `c06d0102` used the approved source-only
scope and the captured Chromium profile. It made **one** content acquisition,
returning **1,344 complete records**, then rejected source authority before any
AAX read. Cleanup and the no-active-page probe passed. It made no model call,
accepted no workflow result and activated no business verifier.

The actual search evidence retains one successful GET response for
`https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045`. The Browser separately
records the document's history-only address with `chaussure+geox+homme+45`.
Both encode the requested query. All raw references are contiguous and preserved.
The diagnostic incorrectly required the document address to equal the terminal
HTTP URL, despite the existing navigation-evidence protocol's separate
`documentUrl`. This is a diagnostic comparator defect, not a demonstrated Browser
transport or observation failure.

The correction reuses the existing navigation verifier's document/response
distinction. It still requires a complete successful acquisition, valid ordered
GET hops, exact requested entry, consistent document/acquisition/snapshot
addresses, and the original query in the requested, observed document and actual
terminal HTTP search URLs. A matching document query cannot replace a different
HTTP search. No URL is normalized, rewritten or used as a replacement entry.

The sanitized history case first fails at the original comparator, then passes.
All **98** affected Release Amazon tests pass with `-warnaserror`, no skips,
including the new different-HTTP-query counterexample. Existing V8/V12 behavior,
navigation grants, financial records and the failed original outcome remain.

Post-correction **offline** inspection of that immutable search admits its source
shape but finds **zero AAX records or eligible entries**. This establishes no AAX
destination, product identity, obsolete-advertisement claim, full source coverage
or business qualification. The earlier retained four-link context remains
unverified. A fresh independently identified source check will validate the
corrected admission path; an absent AAX occurrence must remain an untested AAX
destination, not a reason to repeat searches until a preferred advertisement appears.
