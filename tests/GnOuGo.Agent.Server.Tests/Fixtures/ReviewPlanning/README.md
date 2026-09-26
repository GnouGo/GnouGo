# Retained record-field planning failure

`retained-field-bindings.json` preserves all five model responses from session
`79328cefb7d54d808fa132382f3aee70`, their issued response schemas/request identities,
the initial session/catalog and complete discovery receipts with resolved contracts.
Private local paths and concrete repository URLs were consistently redacted before
export. Types, arguments, ordering, failed references and unauthorized repairs are
unchanged. Original encrypted records and accounting were read through KeyVault
record APIs and left intact. Minification only removes JSON whitespace.

The initial TaskPlan refers to `output(source="item", port=...)`; the first repair
inserts a transform and changes unrelated slots, so it remains rejected. The second
repair binds the entire loop item to five scalar inputs and exhausts two repairs.
The replay uses the original request schemas, including across serialization/recovery,
without editing model responses or replacing contracts with simplified ones.

`synthetic-field-correction.json` is explicitly synthetic. It changes only the final
proposal's five bindings to `field(item, name)` (`endLine` feeds `line`) and declares
`LEFT`/`RIGHT` on the nested `side` type. The test runs this as the initial TaskPlan
in a fresh simulated session after replaying the two discovery responses. It does
not apply an unauthorized revision to the exhausted original session.

Host tests run the real HybridWorkflowPlanner, compiler, validators, YAML and workflow
engine. Inference and all MCP operations are mocked, with actual retained contracts,
faithful response envelopes and independent expected comment arguments/order. No
GitHub comment, repository checkout, Copilot session, command or filesystem deletion
is performed. This is deterministic plumbing evidence, not a live planning reliability
or real Copilot execution claim. The retained review's broader decisions (for example,
its fixed analysis batch) are not silently rewritten or certified by this fix.
