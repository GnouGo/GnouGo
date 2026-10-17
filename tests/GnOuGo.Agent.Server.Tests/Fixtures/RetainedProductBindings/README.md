# Complete retained composition

`failed.json` retains the exhausted structural proposal from
`consumerbindings20261009b-amazon-1`, with local fixture names, a reserved example
host and a disposable output path. Requirement prose is translated for the fixture;
accepted output types, required members, nullability and bounds are retained.
No saved session is loaded or changed by these tests.

`corrected.json` is an explicit offline structural revision using existing bindings:

- One extraction view per complete page and decision stage.
- A pure `foreach` projects related fields from each typed page object, followed
  by one-level `flatten`. Completeness assertions remain on extraction; copying
  does not repeat those assertions or require a runtime iteration.
- Decisions receive compact offered candidates and return observed IDs. Both
  offered-candidate and original-observation lookups are checked. Lookup returns
  an array even for one ID, so actions access records inside an item scope.
- Producer references, URLs and action metadata come from retained original
  observations. Browser still validates reference identity and action compatibility.
- Product normalization is a direct typed `value`. Product records are non-null,
  missing fields may be null, and the maximum is ten. The existing zero-based
  iteration index satisfies the accepted integer contract.
- The bounded TSV formatting transform remains explicit. The workbook path comes
  from the successful Document operation; no literal substitutes for writing.

Both proposals have the same accepted requirements and eight external operation
identities in the same business order. The optional action conditions, product
visits and cleanup remain. There is no sponsored filtering, deduplication or
new ranking. Existing scopes are extended to iterate array-valued lookups. Each optional action
and its adapters use an existing sequence scope, retaining only declared captures
instead of the unrelated preceding execution state.

`LocalProductOutcomeExecutionTests.Retained.cs` discovers actual Browser/Document
contracts over stdio, returns the corrected proposal through the issued model
schema, reaches review, explicitly approves its requirements and executes its
YAML with deterministic inference adapters. A local HTTP catalogue records the
search and product visits. The oracle reads actual XLSX cells independently of
the model adapter. Failed acquisition, unoffered selection, denied writes and
plausible fabricated workbook contents remain failures; they cannot establish
business success. Encrypted committed-receipt recovery must perform no repeat
navigation, inference or write.

This is deterministic local execution evidence, not a successful Amazon/provider
evaluation. Historical paid requests, receipts and reservations are untouched.
