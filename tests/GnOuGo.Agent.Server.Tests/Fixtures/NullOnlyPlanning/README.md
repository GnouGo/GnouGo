# Null-only planning recording

`retained-null-outputs.json` preserves session `ec7ee0aa2d1444d8a3b1fbbc7b8f1558` at revision 10: four issued request identities/schemas and pending checkpoints, three original completed responses, effective settings, exact capability contracts, discovery receipts, diagnostic history and final accounting. Extraction used public encrypted KeyVault record APIs. Private local paths and repository URLs are redacted; the original encrypted session revisions were checked unchanged.

Attempt three has **no completion receipt**. Its retained revision-8 finding is `MODEL_DISPATCH_UNVERIFIABLE` after HTTP 500. The fixture retains that identity, pending checkpoint and diagnostic rather than fabricating a response. The subsequent recorded attempt four produced the final TaskPlan. Four charged calls and zero repairs must remain four calls and zero repairs.

Both `compare_page2_gate` and `compare_page3_gate` explicitly export `nextCursor: null` from their otherwise scopes. Semantic compilation accepts this valid intent, but YAML conversion historically emitted the unsupported shorthand `type: null`, producing both `INVALID_OUTPUT_TYPE` findings.

`RecordedNullOnlyPlanningTests` restores the original pending checkpoints for the three completed receipts and processes their unchanged responses through `HybridWorkflowPlanner`, with source-generated serialization/recovery. It never redispatches the unconfirmed identity or invents a replacement response. The unchanged final proposal reaches review with the saved request settings, usage, discovery receipts and repair permissions. Final review adds the normal incomplete-discovery notices. Approval verification recompiles the artifact, and changing a branch export invalidates it.

No model request, workflow approval, external capability invocation or live execution is performed. Separate generic compiler fixtures verify actual null values, non-null rejection, conditional/captured/group/iteration outputs and recursive authoritative contracts.
