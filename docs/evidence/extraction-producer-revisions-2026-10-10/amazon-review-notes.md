# Fresh planning review history

Run: `extractproducer20261010a-amazon-1`; frozen source `96cbf2176494e2d7c37373a3d08a07a0a1606846`. No artifact approval or execution has occurred.

## Revision 2 — stopped

Two planning calls, zero automatic repairs. The accepted products contract has non-null records with nullable fields, but the proposal made records nullable. `REQUIREMENTS_OUTPUTS_CHANGED` stopped generation. Manual review additionally rejected unrequested actions, guessed selectors, broad snapshot inputs and inference reconstructing products/status. The original response and accounting are retained in `amazon-proposal-r2.json` and `amazon-planning-r2.json`.

The first authorized limited structural revision preserves accepted requirements, search, individual visits, writer and cleanup while changing data adaptation and removing unrequested work. See `amazon-revision-r2.json`.

## Revision 4 — compilation clean, review rejected

Three cumulative calls, one explicit revision, zero automatic repairs. The compiler produced artifact `a3b6020c070b63376c63906ce5c433b6129f497667e6324f1e349fd4c0af7099`. This artifact was not approved.

Review found that learned extraction recreated original records, followed by interpretations which rebuilt the selected action reference and product URLs. Thus deterministic lookup did not carry original action arguments through to dispatch. Product extraction also still received a whole snapshot, and numeric source indices lacked a deterministic producer.

The second limited structural revision replaces those paths with typed per-page original-record projections, offered-ID validation, original-record lookup and direct effectful iteration. It also requests bounded independent extraction of product facts before reconciliation. See `amazon-revision-r4.json`. No compiler/runtime behavior or approval is changed by review feedback.

## Revision 6 — reconnection corrected; narrow follow-up required

Four logical calls, five physical attempts, two explicit revisions and zero automatic repairs. Artifact `e2b138a387ba8cfda9431956181691ff1ef7f8eed99eacf4cd236eb61e334e4d` compiles cleanly but is not approved. Original records are now typed projections, both lookup checks run, and external action arguments come from originals. Product interpretation still repeats the output URL, and completeness flags should be checked before all extraction stages.

The final revision authorizes exactly six existing slots: three local extraction assertions, product-reconciliation inputs/resultType, and the iteration's rows export. It adds no task or operation. See `amazon-revision-r6.json`.

## Revision 8 — ready for concrete artifact review

Five logical calls, six physical attempts, three explicit revisions, zero automatic repairs. Artifact `e6b81b19d8b3c4ccacad4a112ab5e5f4838fd58bdc84e78b4464b28b3e9d41ad` compiles cleanly. The patch changes only the six authorized paths; all accepted requirements and other plan fields are unchanged. Product URL copying is deterministic, completeness assertions precede extraction, and the real writer/cleanup remain intact. See `amazon-review.md`. No execution approval is inferred from clean compilation or the earlier campaign GO.
