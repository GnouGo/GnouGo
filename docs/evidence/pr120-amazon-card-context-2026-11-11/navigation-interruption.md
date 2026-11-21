# Fresh v12 navigation interruption and generic diagnostics

The frozen `9b598d752675c35f1916927c238741ce161132c6` build passes all 1,182
affected Release tests with `-warnaserror` and zero skips. Offline comparison of
the unchanged retained v11 source qualifies its source under v12, preserves the
v11 refusal, and still rejects missing business visits. That historical comparison
creates no live authority or business pass.

The fresh actual-profile `amazon-v12-source-20261111-01` search succeeds with
**1,336 complete records**. Its first exact authorized `/clk/` resolution fails
during navigation after **1,901 ms** with `BROWSER_ERROR`, category
`net::ERR_ABORTED`. The [sanitized receipt](fresh-v12-navigation-interruption.json)
has no navigation evidence. Zero snapshot-capture attempts does not establish
zero HTTP contact or zero observed responses. Source qualification remains false.
Browser close and no-active-page verification pass; cold results are identical.
There are no model calls, accepted plan, execution or workbook in this diagnostic.

The underlying abort is **not explained**. It could precede a response or follow
observed hops; this receipt cannot distinguish them. No proxy, site, permission
or redirect cause is asserted. No timeout, navigation rule, retry or provider is
changed, and no incomplete result is applied.

A separate local test demonstrates a generic diagnostic loss: an exception during
an owned explicit read loses its already observed hop evidence, and the snapshot
wrapper also drops exception-attached navigation evidence. Four controlled tests
inject a Playwright interruption at the existing response checkpoint after actual
one-hop or two-hop localhost GETs, in HTML and complete-snapshot modes. All four
first fail on absent evidence. They reproduce the **diagnostic gap**, not the
underlying real-site abort.

The correction attaches the read's existing observed hops to its original
Playwright exception and preserves that attachment through snapshot wrapping.
The existing tool result reports them with `complete=false` and no final URL.
Error codes and failure categories remain. There is no new request, successful
snapshot, final destination, retry, acceptance or navigation authority. No public
type/schema, protocol or inspector is added. All **190 Browser tests pass** with
`-warnaserror`, zero skipped, retaining policy-denial and redirect negatives.

The next permitted diagnostic is one fresh source identity on the frozen corrected
build. Its precise hypothesis is whether the failed owned navigation leaves any
observed HTTP prefix; the retained incomplete evidence will distinguish that from
an abort before any observed response. Every original own-read/source check and
cleanup still applies. A newly qualified complete source, not the offline source
or these controlled tests, remains mandatory before paid planning.
