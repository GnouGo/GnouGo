# Proposed observed sponsored-host extension

Status: prepared for explicit user review; not authorized or used for live navigation.

Amazon12's complete real search on source `794163d1` contained 1,541 records.
The model selected a literal `https://sponsored-ads.amazon.fr/clk/?…` product link.
The existing delegated guard denied it before transport, and root cleanup closed
the Browser. The prior user authorization covers the separate `/sspa/click`
extension on `amazon.fr` and `www.amazon.fr`; it does not cover this host/path.
No advertising query parameter was decoded into a substitute navigation target.

The proposed isolated policy `amazon-observed-sponsored-host-v2` adds only:

- HTTPS GET of an exact literal `https://sponsored-ads.amazon.fr/clk/?…` entry
  observed in the current complete, successful Amazon.fr search.
- The default HTTPS port, no credentials or fragments, and a nonempty query.
- The same live Browser/source authority; closing the Browser or starting another
  search expires it. Unobserved entries remain denied before transport.
- Every actual redirect destination must satisfy this exact new entry route or
  the already approved Amazon.fr search, `/sspa/click`, and product routes.
  Existing redirect bounds and single-GET/no-transport-retry behavior remain.

Other hosts, wildcard subdomains, other advertising paths, decoded `r` targets,
constructed product URLs, filtering sponsored products and permission bypasses
remain excluded. First-product order, product fields, the workbook, cleanup and
truthful execution evidence remain mandatory.

A separate `amazon-business-v6` verifier requires the new occurrence, source-link
and redirect evidence. Existing v4 and v5 verdicts and authority remain unchanged.
The opt-in is planned as `--amazon-observed-sponsored-host` together with
`--amazon-scope-approval user-strict-amazon-sponsored-host-extension-20261109`.
The grant reference will be used only after an explicit user authorization of
this proposal. It changes neither campaign funding nor saved global provider or
Browser configuration.
