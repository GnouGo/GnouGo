# Checked-contract planning recording

`retained-enum-projection.json` contains the five sanitized original model responses, their issued schemas and request identities, discovery receipts, exact capability contracts, final TaskPlan and diagnostics from session `75d3bce36ebc4f5f9297b39b57953cbe` (revision 14). Extraction used public encrypted KeyVault record APIs; original records, timestamps and accounting were checked unchanged. Private local paths and repository URLs were redacted. No synthetic completion or corrected plan replaces a recorded response.

The first proposal has two nullable comment-input errors. The first repair changes an unrelated input enum and is rejected without replacing its baseline. The final repair fixes the nullable bindings but historically stopped on `MCP_REQUEST_SELECTOR_NOT_LITERAL`: its `value.project` enforces the declared review-event enum, but the validator did not register that checked contract.

`RecordedCheckedContractPlanningTests` replays all five responses through `HybridWorkflowPlanner` using their original request schemas, with serialization/recovery between advances. It verifies rejection immutability, the same final TaskPlan, five calls/two repairs, final review without approval and invalidation after a semantic enum change. These are mocked receipt deliveries, not new inference requests or a claim of live model reliability.

Separate generic tests execute transform → nested typed field selection → MCP selector with renamed capabilities, invalid/missing/null results and ordered iteration. The [clone/review fixture](../ClonePlanning/README.md) supplies the independent full-review execution oracle for MCP payload preservation. No external review or real Copilot execution is performed.
