# Proposed source-only route investigation — not authorized or active

The approved v4/v8 preflight `amazon-v8-source-20261111-01` observed a fresh complete
search with 1,554 records. Three literal `https://sponsored-ads.amazon.fr/clk?…`
links are outside the exact `/clk/?…` grant. Their snapshot references end in
`:record:474`, `:record:476`, and `:record:480`; hashes and context are retained in
[fresh-source-result.json](fresh-source-result.json). No dependent link was fetched.
One has heading context; all share an empty-ASIN div. Their destinations remain
unknown. Neither the heading nor the empty identity establishes a product or store.

## Exact proposed diagnostic delta

Authorize a new **source-only** diagnostic to GET literal
`https://sponsored-ads.amazon.fr/clk?…` entries observed in a fresh complete
successful Amazon.fr search. Additional full-URL route expression:

```text
https://sponsored-ads\.amazon\.fr/clk\?[^#\s]+
```

Each occurrence must be issued exactly as observed, in source order, under a new
identity; repeated links remain separate. Every redirect is checked before contact
against the existing v4 routes plus this single additional route. No new host,
explicit port, fragment, URL normalization, decoded target or constructed URL.
The existing approved `/stores/page/{UUID}` destination scope is unchanged.
Authority expires on close, failure or another search. Close and verify cleanup.

This investigation would distinguish actual product/store destinations from
permission refusal or incomplete evidence. It would not activate a new workflow
policy/verifier, reinterpret heading evidence, omit any product, call a model,
plan a workflow or add budget. V4/v8 and prior versions continue to reject `/clk`.
Any later change to classification or full-workflow navigation needs its own
concrete evidence and explicit authorization. Even all-store destinations would
not waive the current conflicting-heading check.

The original user policy explicitly requires a separate navigation approval;
this proposal is therefore not inferred from the already approved store amendment
or the finite campaign budget. No external message has been sent.
