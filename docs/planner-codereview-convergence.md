# CodeReview convergence validation

Comparison baseline: `bc72dd6b76e979deccaa7f616b830d56b713482a` on
`feat/deterministic-planner-v2`. This is an in-progress report. **No CodeReview
session has yet passed the full release prerequisite of final review, independent
execution fixtures and exact-hash approval.** No end-to-end call saving is claimed
by comparing a stopped baseline with later construction progress.

The live harness invokes Agent.Server's `PlanningSessionService`, the configured
KeyVault model (`gpt-5.5-2026-04-24`, low reasoning), normal encrypted schema-4
persistence and durable reservations/receipts. Request ceilings remain 12,000 input
and 8,192 output tokens, concurrency four and five repairs per workflow/gate.
The isolated tenant is `planner-benchmark`; the original revision-117 session and
full catalog remain unchanged. Private evidence stays encrypted. See the
[fixture provenance](../tests/GnOuGo.Flow.Planning.Tests/Fixtures/README.md) and
[harness instructions](../tests/GnOuGo.Agent.Planning.Benchmark/README.md).

The authorized benchmark policy selects `git_compare_refs → copilot_review` for
review analysis. It is an explicit intent constraint in this harness only, not a
production catalog filter. Runs 05 (revised) onward use this constraint. Production
continues to consider all eligible implementations and never dispatches an
oversized request. Both supplied PR URLs are separate executions of one reusable
workflow, with one owned clone per execution.

## Comparable regressions

- The original operation-op9 matching request fell from **12,810 to 11,475**
  estimated input tokens at the same checkpoint, with identical candidate IDs and
  response schema. Public fixtures reconstruct every original catalog card after
  lossless sharing. Required workflow policies and structural evidence remain present. An intermediate
  11,438-token form incorrectly omitted a governing policy; it is not the retained
  regression result.
- Independent same-node and cross-node holes share one bounded request in tests;
  the sum of individual request estimates is larger. Variable shared obligations
  remain sequential; guaranteed shared artifact provenance permits batching.
- Discriminated unions, reference siblings, finite domains, intersections, tuples,
  nullable elements and additional properties have inclusion-proof regressions.
  Accepted proofs are cross-checked against the runtime schema validator on finite
  domains. Overlapping unions and unsupported proof cases remain closed.
- Schema propagation tests cover nested arrays, call boundaries, loops and adapters
  without model schema calls; authoritative references retain locked constraints.
- Live runs 05 and 06 had correctly placed publication actions but aliases for
  locked activation values. Exact action placement now completes uniquely proven
  outcome values before human review, without a repair request. Valid domain
  values, ambiguous outcomes and all unrelated identities remain unchanged.
- A live loop-source request preceded its body's producer contracts and exposed an
  invalid untyped-array literal. Loop prerequisites now include the body's declared
  external dependencies; the regression dispatches only ready, valid fields.
- Result-schema requests exclude defaults the runtime never applies to outputs,
  removing recursive default-value choices. Structured extraction fields must be
  required in the response schema; nullable fields retain explicit nullability.
- Consumer context contains unresolved argument contracts, excluding unrelated
  optional fields and already established values. Locked contracts remain intact.
- A locked null object previously expanded into unresolved members. It now remains
  null and requires no speculative member assignments.
- Run 08 exposed two computations using already eligible scalar binding identities
  outside the narrower parameter scope. New requests permit those same proven
  scalars as operands of authorized non-artifact computations. A regression accepts
  the original expression without a repair; artifact transformations remain forbidden.
- Run 08's comparison required an original workspace artifact but named only metadata
  in its operation dependencies. The sole available materializer owned by the workflow
  now resolves that required artifact deterministically. Explicit origins, ambiguity,
  borrowed references and locked capabilities retain their existing restrictions.
- Required workflow policies share their classification and requiredness once,
  retaining every policy ID and exact description. Run 09 exposed a 12,173-token
  matching request; this representation removes 384 estimated tokens from that
  checkpoint without narrowing candidates or removing policy evidence.
- On immutable run-08 revision 119, deterministic closure took 134,068 ms before
  per-analysis reuse of contract/provenance results and 3,483 ms afterward. Both
  produced graph fingerprint `a2806d4b0de2951bb0d71ef62475f7bd18502e444f2ae151832d6e7a8e666a64`,
  111 active holes and 34 resolved holes. This is a local preparation-time result,
  not a claim of model-call savings or a completed live session.

- Identical exclusive-union contracts now preserve producer exclusions during inclusion proof. The regression resolves the sole direct binding without a model computation; identical reference text in different schema environments remains distinct.
- Multiple scalar arguments sharing one business input no longer receive that entire input merely because each has one type-compatible binding. The retained choices produce the required separate transformations; existing fully deterministic fixtures keep their zero-call behavior.
- Compiler-owned finalizer guards recognize materializers inside unconditional sequences, including when a later child fails. Behavior implementation validation accepts only the exact derived guard. Executed regressions prove one cleanup after success or a later failure, no cleanup after failed materialization, and unchanged original failure semantics.

- Repeated exact patch value schemas now share local definitions. Behavior revision coordinates use request-local IDs while persisted scopes and schemas retain authority across replay. Response-schema diagnostics no longer repeat large allowed-value enums in prose.
- Reference-only behavior repairs omit executable arguments. Fixed selector descriptions and absent argument metadata are omitted from behavior context; governing constraints, selector values and requiredness remain present. Missing-implementation repairs wait for an invalid existing reference that could satisfy the same requirement.
- At run 13's unchanged preparation checkpoint, initial behavior context fell from
  13,241 to 11,987 estimated input tokens and dispatched successfully (7,468 actual
  input tokens). Its repair then exposed diagnostics indexed against a completed
  review projection instead of the staged candidate. Regressions for top-level,
  nested and finalizer decisions now repair the original node in one request while
  retaining neighboring fields and compiler-owned producer insertion.
- Scoped matching preserves directly declared consumer descriptions and, on
  recovery, already matched downstream implementation identities as read-only
  evidence. Consumer catalogs and response authority remain outside that scope.
  Runs 17 and 18 then passed the previously blocked comparison checkpoint; this
  does not count as full workflow convergence.
- Run 18 repeated revision coordinates with different evidence excerpts twice.
  Asking for one excerpt per target allowed the next bounded scope response to
  pass. Duplicate coordinates remain rejected; the later behavior patches exhausted
  their allowance and the candidate was not approved.

## Retained live attempts

Counts below were observed during work on 2026-09-11. All live attempts are stopped.
Every retry retains its session budget and repair allowance. Runs 01, 09, 13 and 17 have
unverifiable dispatches and are never redispatched. Runs 02–04 predate the explicit
review-implementation constraint. Behavior acceptance is not executable approval.

| Run | Revision | State / phase | Calls | Receipts | Input tokens | Output tokens | Largest reserved estimate |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 01 | 22 | recovery / capabilities | 8 | 7 | 26075 | 7167 | 11985 |
| 02 | 133 | unsupported / capabilities | 41 | 41 | 183550 | 33041 | 11963 |
| 03 | 100 | recovery / repair | 30 | 30 | 91929 | 33135 | 11965 |
| 04 | 90 | recovery / capabilities | 27 | 27 | 129140 | 21934 | 11963 |
| 05 | 193 | recovery / repair | 57 | 57 | 200127 | 62411 | 12000 |
| 06 | 120 | recovery / construction | 35 | 35 | 115807 | 66000 | 11962 |
| 07 | 74 | behavior_review / behavior | 24 | 24 | 99138 | 23272 | 11977 |
| 08 | 144 | recovery / repair | 46 | 46 | 185511 | 40739 | 11998 |
| 09 | 159 | recovery / construction | 47 | 46 | 234838 | 31908 | 11955 |
| 10 | 168 | recovery / behavior | 51 | 51 | 260385 | 48179 | 11941 |
| 11 | 68 | unsupported / capabilities | 20 | 20 | 99860 | 15703 | 11937 |
| 12 | 89 | unsupported / capabilities | 24 | 24 | 122502 | 33755 | 11952 |
| 13 | 96 | recovery / repair | 31 | 30 | 126104 | 21874 | 11991 |
| 14 | 72 | unsupported / capabilities | 22 | 22 | 93667 | 20438 | 11995 |
| 15 | 77 | recovery / behavior | 26 | 26 | 137793 | 24898 | 11824 |
| 16 | 138 | unsupported / capabilities | 43 | 43 | 209533 | 34827 | 11977 |
| 17 | 85 | recovery / behavior | 25 | 24 | 109784 | 16576 | 11858 |
| 18 | 182 | recovery / behavior | 55 | 55 | 280216 | 47590 | 11901 |
| 19 | 76 | recovery / capabilities | 21 | 21 | 105885 | 33827 | 11950 |
| 20 | 64 | unsupported / capabilities | 20 | 20 | 100179 | 17489 | 11923 |

- 01: provider connection failed after seven verifiable receipts; the remaining dispatch is unverifiable.
- 02: matching incorrectly required a cleanup primitive to implement workflow finalization; compiler-owned evidence is now excluded from intrinsic matching requirements.
- 03: an optional structured-output member was accepted before the complete local gate; the next staged delta correctly refused permission to edit that earlier field. New response schemas and immediate validation prevent this defect. The old staged candidate is retained.
- 04: the proposed behavior assumed a toolchain. It was not approved. Clarified runtime manifest discovery then reached another preparation input ceiling.
- 05: construction reached the cleanup dependency gate, which correctly rejected a finalizer without its owned-path argument. The old frozen skeleton had silently omitted optional arguments; exact repair could not add one. The staged candidate remains intact. Fresh skeletons expose optional argument holes and distinguish omission from null.
- 06: response truncations consumed the same workflow allowance. Narrowed schema context allowed the first structured metadata candidate to complete; a later response again hit the output ceiling. This is not a successful run.
- 07: behavior prose claimed the authorized review chain, but locked capabilities selected a different implementation. It was not accepted. Required workflow policies are now retained in scoped capability selection and matching requests, and the benchmark checks locked methods before accepting behavior.
- 08: the required chain was locked and behavior review passed. Behavior compaction reduced its estimate from 12,719 to 11,651. Construction exhausted five typed repairs; its retained failing expression contains unquoted prose with interpolation. The candidate and exhausted allowance remain intact. New request schemas exclude that lexical form before dispatch; AST, provenance and runtime validation remain mandatory. This is not an executable success.
- 09: policy sharing cleared a 12,173-token matching blocker. The clarified implementation policy reached behavior review and construction. Request 47 returned HTTP 500 without a verifiable receipt; the session is stopped without redispatch. Its earlier assignments exposed an incorrect forced identity for several scalar arguments; the regression now retains those genuine transformation choices.
- 10: the first behavior response reached 8,192 output tokens; a bounded retry reached behavior review. Review rejected a test implementation whose declared arguments cannot target the owned clone. Its persisted revision retained all budgets. Compaction cleared revision-scope and patch-request input blockers, but five behavior repairs failed monotonic acceptance and exhausted that gate. The prior candidate is retained.
- 11: catalog compaction cleared a 12,257-token manifest-matching blocker; the largest reserved estimate is 11,937. Matching still rejected cleanup by requiring the primitive to implement finalization itself. No behavior or executable approval has been granted.

- 12: an exact inventory excerpt failed evidence validation; bounded recovery progressed through matching and an output truncation. Review and cleanup contracts remain unresolved.
- 13: preparation completed, but the first behavior request needed 13,241 estimated input tokens. Compaction cleared that blocker. The diagnostic-coordinate fix reached behavior review in one subsequent exact repair, and behavior was accepted. Construction retained an invalid prose expression and an empty repair response. Request 31 returned HTTP 400 without a verifiable receipt; it is stopped without redispatch. The exact cause of that provider rejection was not retained by the redacted transport error.
- 14: inventory repair needed 12,018 input tokens. Sharing its repeated evidence schema allowed bounded recovery; comparison and cleanup matching remain unresolved.
- 15: initial behavior omitted three implementations and selected one capability outside its ownership. Five scoped repairs introduced invalid business-input dependencies and were rejected. The allowance is exhausted and retained. New insertion schemas restrict ownership and business-input names before dispatch.

- 16: behavior review selected a configured command that could not establish clone-scoped integration-test execution. It was not accepted. Its persisted revision stopped on unresolved lint execution contracts with unchanged budgets.
- 17: comparison matching passed after adding the declared downstream boundary. Initial behavior needed repair; request 25 received HTTP 400 without a verifiable receipt and is never redispatched. No behavior has been accepted.
- 18: behavior review selected configured unit/integration test commands without proof of the owned working directory and treated manifest filenames as manifest contents. It has not been accepted. Its revision passed comparison matching once the already matched downstream implementation was supplied as read-only evidence. Two revision-scope responses repeated targets and were rejected. The third scope passed, but the subsequent behavior repairs exhausted five attempts without a valid accepted candidate.
- 19: capability matching stopped twice at the 8,192-token output ceiling. The last matching-repair receipt contains no text or structured candidate. Revision 76 is stopped with `MODEL_OUTPUT_LIMIT` at `/preparation`; no behavior plan or graph exists. All 21 dispatches have receipts. No further live request is authorized.
- 20: the single fresh run from frozen `a2b81f3` stopped on cleanup capability matching. Its complete receipt required catalog entries to implement finalizer scheduling and preserve original failures. The inventory had already classified both as workflow structure. No behavior plan, graph, executable validation or approval was reached; no retry or second live session followed.

Session identities, for the encrypted evidence ledger:

- 01: `08bd95da593f4a0ba9e94b86b653b74d`
- 02: `89faf6779b484a4ea73ec76195c9f924`
- 03: `5c40756a5506401fa3c5cde0e0e29d7f`
- 04: `c390530f72114220bf048f781b9546c4`
- 05: `c4d90fef06b742e8b147c565563e0b2a`
- 06: `c252fcd1384248339401a183df5fe323`
- 07: `e05e2150e3e34ff8a08030dd58966ecc`
- 08: `08ce4e7e6fef4653be727078cbd52313`
- 09: `bc8b235024e54693bc31cd84763c15d9`
- 10: `57c836fbd68d4e65908949856e0f2b63`
- 11: `850a38fb2c6e4f9eb636d0c21672a06e`
- 12: `4cbb38f8952a449bbe76c3ab0b358d80`
- 13: `e1adbb441e0a42ea8898ad9bfb33ad22`
- 14: `4e753d88a123409d893169c2b3dba1a1`
- 15: `6968045bd9764b0b86b8d2ca10ae86e5`
- 16: `458146849ffc40f4a13fb93373d564b9`
- 17: `c3c0dd776c404bc1a116dcdf36374894`
- 18: `4c75e60116264d2791a21cf3ae248ee2`
- 19: `e3fa76e61c51468787b27470eae09eba`
- 20: `43c7d728e6c54455b1abb3a706689dfb`

## Request attribution by workflow, phase and gate

`$plan` denotes work without a workflow owner. Unknown historical attribution or
unavailable usage is shown as “unknown”, not zero. Reservations include pending
requests; only verifiable receipts count as model use. Repeated observations and
receipt replay do not increment attribution. Detailed identities remain in traces,
not metric dimensions.

| Run | Workflow | Phase | Gate | Reservations / receipts | Estimated input | Actual input / output | Repairs / failures |
| --- | --- | --- | --- | --- | ---: | --- | --- |
| 01 | $plan | intent | response_contract | 2 / 2 | 3695 | 1991 / 1102 | unknown / unknown |
| 01 | $plan | intent_repair | response_contract | 1 / 1 | 2662 | 1584 / 558 | unknown / unknown |
| 01 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6221 | 3117 / 2363 | unknown / unknown |
| 01 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 7602 | 4192 / 1888 | unknown / unknown |
| 01 | $plan | workflow.plan.capability_candidates | response_contract | 3 / 2 | 35202 | unknown / unknown | unknown / unknown |
| 02 | $plan | intent | response_contract | 2 / 2 | 3553 | 1790 / 493 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 12585 | 6270 / 6602 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 18177 | 9968 / 5286 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_candidates | response_contract | 9 / 9 | 88611 | 55659 / 11162 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_matching | response_contract | 20 / 20 | 136099 | 83975 / 5363 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_matching_repair | response_contract | 3 / 3 | 17491 | 10791 / 1258 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 10937 | 6777 / 633 | unknown / unknown |
| 02 | $plan | workflow.plan.capability_coverage_gap_adjudication | response_contract | 1 / 1 | 1977 | 1051 / 219 | unknown / unknown |
| 02 | $plan | behavior | response_contract | 1 / 1 | 11869 | 7269 / 2025 | unknown / unknown |
| 03 | $plan | intent | response_contract | 1 / 1 | 1920 | 972 / 304 | unknown / unknown |
| 03 | $plan | intent_repair | response_contract | 1 / 1 | 2664 | 1395 / 357 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6436 | 3212 / 3105 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 9063 | 5007 / 3276 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 38290 | 24268 / 2013 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_matching | response_contract | 11 / 11 | 60213 | 36574 / 3204 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 3131 | 1790 / 251 | unknown / unknown |
| 03 | $plan | workflow.plan.capability_coverage_gap_adjudication | response_contract | 1 / 1 | 1337 | 606 / 74 | unknown / unknown |
| 03 | $plan | behavior | response_contract | 1 / 1 | 11965 | 7816 / 2347 | unknown / unknown |
| 03 | review_single_pr | construction | response_contract | 8 / 8 | 19583 | 10289 / 18204 | unknown / unknown |
| 04 | $plan | intent | response_contract | 2 / 2 | 3945 | 2000 / 373 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 12977 | 6480 / 6949 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 19997 | 10961 / 6367 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 79846 | 49936 / 3926 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_matching | response_contract | 10 / 10 | 72659 | 44768 / 1691 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 10207 | 6454 / 746 | unknown / 0 |
| 04 | $plan | workflow.plan.capability_coverage_gap_adjudication | response_contract | 1 / 1 | 2713 | 1568 / 163 | unknown / 0 |
| 04 | $plan | behavior | response_contract | 1 / 1 | 11420 | 6973 / 1719 | unknown / 0 |
| 05 | $plan | intent | response_contract | 2 / 2 | 4145 | 2105 / 763 | unknown / 0 |
| 05 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13177 | 6585 / 7276 | unknown / 0 |
| 05 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 20673 | 11282 / 6470 | unknown / 0 |
| 05 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 80033 | 50080 / 4389 | unknown / 0 |
| 05 | $plan | intent_repair | response_contract | 1 / 1 | 3078 | 1614 / 377 | 0 / 0 |
| 05 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 64878 | 40028 / 3003 | 0 / 0 |
| 05 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2199 | 1105 / 250 | 0 / 0 |
| 05 | $plan | behavior | response_contract | 1 / 1 | 11906 | 7524 / 2122 | 0 / 0 |
| 05 | review_single_pull_request | behavior_repair | behavior_contract | 2 / 2 | 17010 | 11004 / 345 | 2 / 2 |
| 05 | review_single_pull_request | construction | response_contract | 23 / 23 | 96923 | 62805 / 35608 | 2 / 2 |
| 05 | review_single_pull_request | repair | typed_dataflow | 3 / 3 | 9065 | 5995 / 1808 | 3 / 6 |
| 05 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 05 | review_single_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 05 | review_single_pull_request | construction | typed_dataflow | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 06 | $plan | intent | response_contract | 1 / 1 | 2120 | 1077 / 297 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6636 | 3317 / 3497 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 10082 | 5553 / 2984 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 39012 | 24512 / 1394 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 66562 | 40896 / 3182 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_matching_repair | response_contract | 2 / 2 | 11168 | 6777 / 492 | 0 / 0 |
| 06 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2193 | 1103 / 270 | 0 / 0 |
| 06 | $plan | behavior | response_contract | 1 / 1 | 11882 | 7521 / 3014 | 0 / 0 |
| 06 | automatic_pr_review | behavior_repair | behavior_contract | 1 / 1 | 9432 | 6182 / 434 | 1 / 1 |
| 06 | automatic_pr_review | construction | response_contract | 11 / 11 | 33961 | 18869 / 50436 | 5 / 6 |
| 06 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 06 | automatic_pr_review | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 07 | $plan | intent | response_contract | 1 / 1 | 2120 | 1077 / 433 | 0 / 0 |
| 07 | $plan | intent_repair | response_contract | 1 / 1 | 2909 | 1528 / 354 | 1 / 0 |
| 07 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6636 | 3317 / 3346 | 0 / 0 |
| 07 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 10041 | 5531 / 3216 | 1 / 0 |
| 07 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 39072 | 24676 / 1555 | 0 / 0 |
| 07 | $plan | workflow.plan.capability_matching | response_contract | 11 / 11 | 63485 | 39381 / 2581 | 0 / 0 |
| 07 | $plan | workflow.plan.capability_matching_repair | response_contract | 1 / 1 | 7400 | 4469 / 617 | 1 / 0 |
| 07 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2140 | 1063 / 260 | 0 / 0 |
| 07 | $plan | behavior | response_contract | 2 / 2 | 21382 | 13408 / 10400 | 1 / 1 |
| 07 | review_single_pull_request | behavior_repair | behavior_contract | 1 / 1 | 7295 | 4688 / 510 | 1 / 0 |
| 07 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 07 | review_single_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 08 | $plan | intent | response_contract | 1 / 1 | 2120 | 1077 / 362 | 0 / 0 |
| 08 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6636 | 3317 / 5372 | 0 / 0 |
| 08 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 11014 | 6001 / 3564 | 1 / 0 |
| 08 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 43520 | 26900 / 2233 | 0 / 0 |
| 08 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 86392 | 51589 / 4962 | 0 / 0 |
| 08 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2325 | 1190 / 100 | 0 / 0 |
| 08 | $plan | behavior | response_contract | 1 / 1 | 11651 | 7088 / 2848 | 0 / 0 |
| 08 | automatic_single_pr_review | construction | response_contract | 20 / 20 | 100835 | 67553 / 19011 | 1 / 1 |
| 08 | automatic_single_pr_review | repair | typed_dataflow | 5 / 5 | 30292 | 20796 / 2287 | 5 / 6 |
| 08 | automatic_single_pr_review | construction | typed_dataflow | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 09 | $plan | intent | response_contract | 2 / 2 | 4339 | 2202 / 510 | 0 / 0 |
| 09 | $plan | intent_repair | response_contract | 1 / 1 | 2887 | 1512 / 377 | 1 / 0 |
| 09 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13371 | 6682 / 6915 | 0 / 0 |
| 09 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 20191 | 11095 / 6078 | 2 / 0 |
| 09 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 83762 | 52360 / 5418 | 0 / 0 |
| 09 | $plan | workflow.plan.capability_matching | response_contract | 23 / 23 | 208536 | 130691 / 7985 | 0 / 0 |
| 09 | $plan | workflow.plan.capability_matching_repair | response_contract | 2 / 2 | 19871 | 12640 / 168 | 2 / 0 |
| 09 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2995 | 1663 / 179 | 0 / 0 |
| 09 | $plan | behavior | response_contract | 1 / 1 | 10445 | 6390 / 2793 | 0 / 0 |
| 09 | review_single_pull_request | construction | response_contract | 4 / 3 | 11010 | unknown / unknown | 0 / 2 |
| 09 | review_single_pull_request | repair | response_contract | 1 / 1 | 6410 | 5160 / 576 | 1 / 0 |
| 10 | $plan | intent | response_contract | 2 / 2 | 4677 | 2372 / 777 | 0 / 0 |
| 10 | $plan | intent_repair | response_contract | 1 / 1 | 3098 | 1621 / 367 | 1 / 0 |
| 10 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13709 | 6852 / 7551 | 0 / 0 |
| 10 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 22526 | 12371 / 7355 | 2 / 0 |
| 10 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 87353 | 54452 / 4737 | 0 / 0 |
| 10 | $plan | workflow.plan.capability_matching | response_contract | 24 / 24 | 167751 | 102149 / 7872 | 0 / 0 |
| 10 | $plan | workflow.plan.capability_coverage_review | response_contract | 2 / 2 | 10144 | 6035 / 868 | 0 / 0 |
| 10 | $plan | behavior | response_contract | 2 / 2 | 23462 | 14480 / 12522 | 1 / 1 |
| 10 | $plan | workflow.plan.capability_matching_repair | response_contract | 1 / 1 | 5403 | 3382 / 402 | 1 / 0 |
| 10 | $plan | behavior_revision_scope | response_contract | 1 / 1 | 10724 | 9420 / 600 | 0 / 0 |
| 10 | review_single_pr_with_human_publication_gate | behavior_repair | response_contract | 1 / 1 | 10333 | 7671 / 1425 | 1 / 0 |
| 10 | review_single_pr_with_human_publication_gate | behavior_repair | behavior_contract | 5 / 5 | 59695 | 39580 / 3703 | 5 / 5 |
| 10 | review_single_pr_with_human_publication_gate | behavior | response_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 10 | review_single_pr_with_human_publication_gate | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 11 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 97 | 0 / 0 |
| 11 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6735 | 3365 / 3831 | 0 / 0 |
| 11 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 11296 | 6190 / 3624 | 1 / 0 |
| 11 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 44629 | 27564 / 2317 | 0 / 0 |
| 11 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 93154 | 58118 / 5748 | 0 / 0 |
| 11 | $plan | workflow.plan.capability_matching_repair | response_contract | 1 / 1 | 5638 | 3498 / 86 | 1 / 0 |
| 12 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 97 | 0 / 0 |
| 12 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13470 | 6730 / 7045 | 0 / 0 |
| 12 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 21689 | 11975 / 6853 | 2 / 0 |
| 12 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 43860 | 27440 / 5313 | 0 / 0 |
| 12 | $plan | workflow.plan.capability_matching | response_contract | 13 / 13 | 106635 | 66613 / 13578 | 0 / 0 |
| 12 | $plan | workflow.plan.capability_matching_repair | response_contract | 2 / 2 | 14161 | 8619 / 869 | 2 / 0 |
| 13 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 232 | 0 / 0 |
| 13 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6735 | 3365 / 4211 | 0 / 0 |
| 13 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 11929 | 6541 / 4204 | 1 / 0 |
| 13 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 45290 | 27808 / 2312 | 0 / 0 |
| 13 | $plan | workflow.plan.capability_matching | response_contract | 13 / 13 | 92528 | 56587 / 4984 | 0 / 0 |
| 13 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2239 | 1129 / 229 | 0 / 0 |
| 13 | $plan | behavior | response_contract | 1 / 1 | 11987 | 7468 / 3450 | 0 / 0 |
| 13 | review_single_github_pull_request | behavior_repair | behavior_contract | 3 / 3 | 16343 | 10450 / 367 | 3 / 2 |
| 13 | review_single_github_pull_request | construction | response_contract | 3 / 3 | 8890 | 4664 / 977 | 0 / 2 |
| 13 | review_single_github_pull_request | repair | response_contract | 3 / 2 | 10714 | unknown / unknown | 3 / 1 |
| 13 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 13 | review_single_github_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 14 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 96 | 0 / 0 |
| 14 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13402 | 6725 / 9276 | 0 / 0 |
| 14 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 10560 | 5886 / 3550 | 1 / 0 |
| 14 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 42025 | 26204 / 2334 | 0 / 0 |
| 14 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 76572 | 47075 / 4707 | 0 / 0 |
| 14 | $plan | workflow.plan.capability_matching_repair | response_contract | 2 / 2 | 10888 | 6652 / 475 | 2 / 0 |
| 15 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 85 | 0 / 0 |
| 15 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6735 | 3365 / 3769 | 0 / 0 |
| 15 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 10690 | 5866 / 3483 | 1 / 0 |
| 15 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 42195 | 26308 / 2754 | 0 / 0 |
| 15 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 94483 | 59084 / 9057 | 0 / 0 |
| 15 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2148 | 1066 / 194 | 0 / 0 |
| 15 | $plan | behavior | response_contract | 1 / 1 | 11733 | 7359 / 4413 | 0 / 0 |
| 15 | review_single_pull_request | behavior_repair | behavior_contract | 5 / 5 | 52230 | 33620 / 1143 | 5 / 5 |
| 15 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 15 | review_single_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 16 | $plan | intent | response_contract | 2 / 2 | 4633 | 2349 / 652 | 0 / 0 |
| 16 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13529 | 6819 / 7678 | 0 / 0 |
| 16 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 22771 | 12509 / 7690 | 2 / 0 |
| 16 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 88732 | 54856 / 4170 | 0 / 0 |
| 16 | $plan | workflow.plan.capability_matching | response_contract | 24 / 24 | 177649 | 109308 / 10409 | 0 / 0 |
| 16 | $plan | workflow.plan.capability_matching_repair | response_contract | 2 / 2 | 20391 | 12924 / 905 | 2 / 0 |
| 16 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2203 | 1111 / 393 | 0 / 0 |
| 16 | $plan | behavior | response_contract | 1 / 1 | 10669 | 6715 / 2849 | 0 / 0 |
| 16 | review_single_pull_request | behavior_repair | behavior_contract | 1 / 1 | 4589 | 2942 / 81 | 1 / 0 |
| 16 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 16 | review_single_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 17 | $plan | intent | response_contract | 1 / 1 | 2219 | 1125 / 103 | 0 / 0 |
| 17 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6667 | 3360 / 3477 | 0 / 0 |
| 17 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 10468 | 5783 / 3183 | 1 / 0 |
| 17 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 42331 | 26292 / 2780 | 0 / 0 |
| 17 | $plan | workflow.plan.capability_matching | response_contract | 13 / 13 | 92648 | 57140 / 3753 | 0 / 0 |
| 17 | $plan | workflow.plan.capability_matching_repair | response_contract | 3 / 3 | 15778 | 9496 / 891 | 3 / 0 |
| 17 | $plan | behavior | response_contract | 1 / 1 | 10457 | 6588 / 2389 | 0 / 0 |
| 17 | review_single_pull_request | behavior_repair | behavior_contract | 1 / 0 | 9989 | unknown / unknown | 1 / 0 |
| 17 | $plan | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 17 | review_single_pull_request | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 18 | $plan | intent | response_contract | 2 / 2 | 4697 | 2380 / 726 | 0 / 0 |
| 18 | $plan | intent_repair | response_contract | 1 / 1 | 2985 | 1559 / 369 | 1 / 0 |
| 18 | $plan | workflow.plan.capability_inventory | response_contract | 2 / 2 | 13592 | 6850 / 7798 | 0 / 0 |
| 18 | $plan | workflow.plan.capability_inventory_repair | response_contract | 2 / 2 | 22943 | 12765 / 7827 | 2 / 0 |
| 18 | $plan | workflow.plan.capability_candidates | response_contract | 8 / 8 | 86084 | 53672 / 4517 | 0 / 0 |
| 18 | $plan | workflow.plan.capability_matching | response_contract | 25 / 25 | 185997 | 115355 / 10056 | 0 / 0 |
| 18 | $plan | workflow.plan.capability_matching_repair | response_contract | 4 / 4 | 24271 | 14578 / 1066 | 4 / 0 |
| 18 | $plan | behavior | response_contract | 1 / 1 | 10380 | 6531 / 3611 | 0 / 0 |
| 18 | $plan | workflow.plan.capability_coverage_review | response_contract | 1 / 1 | 2197 | 1117 / 200 | 0 / 0 |
| 18 | $plan | behavior_revision_scope | response_contract | 3 / 3 | 23353 | 21149 / 2421 | 0 / 0 |
| 18 | pr_review_with_human_publish_gate | behavior_repair | response_contract | 1 / 1 | 5194 | 3425 / 614 | 1 / 0 |
| 18 | pr_review_with_human_publish_gate | behavior_repair | behavior_contract | 5 / 5 | 57655 | 40835 / 8385 | 5 / 5 |
| 18 | pr_review_with_human_publish_gate | behavior | response_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 18 | pr_review_with_human_publish_gate | behavior | behavior_contract | 0 / 0 | 0 | 0 / 0 | 0 / 1 |
| 19 | $plan | intent | response_contract | 1 / 1 | 2443 | 1234 / 97 | 0 / 0 |
| 19 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6891 | 3469 / 3422 | 0 / 0 |
| 19 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 11099 | 6160 / 3656 | 1 / 0 |
| 19 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 42593 | 26436 / 3227 | 0 / 0 |
| 19 | $plan | workflow.plan.capability_matching | response_contract | 13 / 13 | 102120 | 63313 / 15233 | 0 / 0 |
| 19 | $plan | workflow.plan.capability_matching_repair | response_contract | 1 / 1 | 8846 | 5273 / 8192 | 1 / 0 |
| 20 | $plan | intent | response_contract | 1 / 1 | 2443 | 1234 / 357 | 0 / 0 |
| 20 | $plan | workflow.plan.capability_inventory | response_contract | 1 / 1 | 6891 | 3469 / 3900 | 0 / 0 |
| 20 | $plan | workflow.plan.capability_inventory_repair | response_contract | 1 / 1 | 11605 | 6302 / 3541 | 1 / 0 |
| 20 | $plan | workflow.plan.capability_candidates | response_contract | 4 / 4 | 44817 | 27508 / 2775 | 0 / 0 |
| 20 | $plan | workflow.plan.capability_matching | response_contract | 12 / 12 | 95018 | 57996 / 6658 | 0 / 0 |
| 20 | $plan | workflow.plan.capability_matching_repair | response_contract | 1 / 1 | 6055 | 3670 / 258 | 1 / 0 |

## Hole accounting

| Run | Workflow | Active holes | Deterministic | Distinct model holes / exposures | Schema deterministic / model | Model required / used |
| --- | --- | ---: | ---: | --- | --- | --- |
| 03 | review_single_pr | 44 | 4 | 16 / 22 | 1 / 4 | unknown / 16 |
| 05 | review_single_pull_request | 49 | 11 | 38 / 44 | 0 / 10 | 38 / 38 |
| 06 | automatic_pr_review | 51 | 4 | 11 / 22 | 0 / 4 | unknown / 11 |
| 08 | automatic_single_pr_review | 111 | 9 | 62 / 91 | 0 / 5 | unknown / 62 |
| 09 | review_single_pull_request | 92 | 3 | 10 / 12 | 0 / 3 | unknown / 9 |
| 13 | review_single_github_pull_request | 102 | 1 | 5 / 8 | 0 / 3 | unknown / 5 |

## Offline continuation and evidence boundary

Live execution stopped after run 19, per the user's instruction. The new benchmark
`replay` command uses the existing planner and runtime with a receipt-only test
transport. It reads schema-4 records, opens the EF index read-only, creates no live
provider or persistence writer, and stops at human review or missing evidence.
It neither resets persisted allowances nor turns local accounting into live usage.

Replaying immutable revision **74** of run 19 (the pending call-21 reservation)
produced the same first blocker: `MODEL_OUTPUT_LIMIT` at `/preparation`, after
one local advance and one replayed receipt, with **zero provider dispatches**.
The saved revision, call index and encrypted budget were verified unchanged.
The repair scope and candidate validation use the current production code; no
complete matching candidate exists to patch locally. The earlier captured matching
decisions still report unresolved review artifact input and cleanup capability
coverage. Those model decisions are not authority to infer missing operation edges,
change classifications or grant new capabilities. No speculative production fix was
added for the truncated response.

New generic regressions cover truncated matching repair with both null and
schema-valid partial JSON, retained decisions, a single request and recorded usage;
receipt replay isolation; changed content, identities and owners; missing or
unverifiable evidence; and cancellation. Existing downstream-context regression
continues to prove that retained implementation evidence grants no response authority
over the consumer. The preceding fixes and their comparable results are listed above.

This offline continuation passed 16 matching tests and 53 Agent.Server planning
tests (including ten new replay/truncation cases), the benchmark build with warnings
treated as errors, and all 18 frozen fixture self-checks. The fixture self-checks
still do not constitute execution of a generated workflow. No provider call was
made during this continuation. Broader release checks remain pending below.

The next missing evidence is a complete response to the matching-repair scope whose
captured estimate is **8,846 input tokens**, with an unchanged **8,192 output-token
ceiling** (the failed receipt recorded 5,273 actual input tokens). This describes
one request, not a forecast for the full session: remaining preparation, behavior,
construction and validation calls are unknown. Repeating the same request may
truncate again. Any live continuation requires explicit approval; no new session
will start automatically. Overall convergence remains **0 successful live runs**.

## Single-session validation from frozen commit

The next user-authorized validation froze **`a2b81f365de1f18a3e067e7c2723bbdbfcbd1797`**
after the full offline planning CI test set passed: 584 Planning, 63 Integrations,
841 Flow.Core and 321 Agent.Server tests (**1,809 total**). No deterministic
regression was found and no production code changed before the live run.
The planning NuGet package and benchmark host built with warnings treated as errors.
The published macOS ARM64 Native AOT planning/encrypted-persistence smoke passed
under the existing documented Jint/Darwin publish exceptions.

All 18 frozen transport fixture self-checks passed. Receipt-only replays of run 19
revision 74, run 13 revision 95 and run 17 revision 84 reproduced their recorded
output-limit or unverifiable-receipt boundaries with zero provider dispatches and
unchanged persisted state. Immutable run 08 revision 119 retained graph fingerprint
`a2806d4b0de2951bb0d71ef62475f7bd18502e444f2ae151832d6e7a8e666a64`
and all 34 resolved fields among 111 active holes.

The frozen catalog fingerprint remains
`792eebc5c700c51dd30fa69fd4680dee8b65fb8b8bc35ed9611bd2a61762fc43`.
The unchanged benchmark policy file SHA-256 is
`b92002cf07ef660981dc5c758d775be40589045d14df706cf454ca3c58e52604`.
One fresh session, `CodeReviewConvergence20_a2b81f3`, was authorized after these
checks. A failure permits offline diagnosis and repair, not another live start.
Only complete independent execution and exact-hash approval satisfy a successful
session; preparation or behavior review alone does not.

Run 20 ended at revision 64 after **20 calls / 117,668 tokens**, with all receipts
persisted. The first blocker is `CAPABILITY_CONTRACT_UNRESOLVED` at
`/preparation/matching_issues/0`, for the required owned-resource cleanup operation.
Classification: a matching-request responsibility error followed by a semantic
model rejection, not a token ceiling, transport error or compiler failure. The
matching prompt explicitly delegated workflow enforcement to capability composition,
although the captured inventory marked these obligations as `workflow_structure`.

The offline correction moves those exact classified excerpts into a separate
`planner_owned_requirements` map and assigns enforcement to behavior validation,
construction and compilation. Intrinsic requirements, artifact arguments, policies,
catalog scope and the response schema retain their authority. No names or keywords
are inspected and no captured match is promoted to success. Two new generic cases
(initial matching and repair with exclusively structural evidence) failed before
the fix and passed afterward; all **586 planning tests** and **53 Agent.Server
planning tests** passed, and the benchmark host rebuilt without warnings.
Receipt-only replay from run 20 revision 60 consumed its one captured receipt and
retained the same unavailable result with zero provider calls and unchanged saved
session, call index and budget. Replay validates the captured response; it cannot
predict a new response to the corrected request.

This corrects the responsibility expressed by the request. It does not establish
that a future response will select a valid owned-path deletion contract: the recorded
response also questioned that proof. Its unavailable result remains preserved.
No independent execution or exact-hash approval is possible without a completed
artifact. Another live run is not authorized by this failed attempt.

## Bounded convergence series, 2026-09-12

The user authorized at most three fresh sessions, with the full offline suite before
each and a stop after one complete success, repeated blocker class or required human
clarification. The first run used `42885c677d037df9b7b25789d35afcacccd85bcd`.
All 1,811 offline tests passed (586 Planning, 63 Integrations, 841 Core, 321 Server),
as did the package/build checks, 18 fixture self-checks, four captured failure replays
and the published Native AOT planning/persistence smoke. Catalog and policy stayed
frozen; production code was unchanged.

Fresh session 1 (ledger run 21), `be73a018a5494d01b40e7da869fc9c75`, stopped at
revision 10 after four calls and 21,536 tokens (12,419 input / 9,117 output).
The largest reserved input estimate was 11,153. All four receipts are verifiable.
Both inventory responses changed a singular source word to a plural and therefore
failed exact-evidence validation at the same coverage coordinate. Classification:
model transcription failure, not a parser or normalization defect. The mandatory
evidence check remains intact; no production change is justified by this result.
Three generic regression cases reproduce repeated inflection/case changes remaining
invalid while an exact source citation passes. No catalog matching, behavior plan
or executable graph was reached, and this session was not retried.

The general receipt-only replay consumed the initial inventory receipt and stopped
at its next unmatched reservation identity. Direct offline inventory validation is
used to check both captured responses; this does not claim a complete session replay
or invent a replacement response.

Before fresh session 2, all **1,814 tests** passed (589 Planning, 63 Integrations,
841 Core, 321 Server). The 18 frozen fixture self-checks and the existing published
Native AOT planning/persistence smoke passed again; production binaries are unchanged.
The first failure is retained as a model-evidence failure, without a speculative
production fix or a retry of its session.

## Outstanding acceptance

Three independent successful sessions, both PR execution fixtures (including
failure cleanup and rejected publication), exact-hash approval, final solution /
frontend / package checks, published trimmed and Native AOT persistence smokes,
PR #99 update and GitHub Actions verification remain pending. The independent
execution harness has synthetic frozen observations and passed 18 offline fixture
self-checks against producer schemas. These self-checks are not executable workflow
validation; no final-review artifact has yet run those cases.
