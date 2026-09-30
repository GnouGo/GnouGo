# Product-query planning against producer contracts

Issue #112 / draft PR #113. This is one authorized diagnosis and verification, not a new benchmark campaign or permission to iterate live outcomes.

## Frozen request and comparison

> Input: a product to search, e.g. ‘chaussure geox homme 45’. Go to Amazon, search for it, list products, visit each product page, extract name/description/price, and save the results as an XLSX file on disk.

Three distinct planning-only identities: unchanged candidate (production tree `bbd07b9`), isolated main `bf90eb6d5048bd80ba83cb70df69b841007d4d59`, and one frozen corrected candidate. No replacement sessions. Browser/Document producer registrations supply their complete metadata; the first two runs share an encrypted snapshot. No hand-authored TaskPlan, prebuilt search URL, tool invocation, workflow approval or execution is supplied by the diagnosis harness.

Model `gpt-5.5-2026-04-24`, medium reasoning, 24,000 input / 8,192 output request limits, eight physical attempts and two repairs per session. Existing campaign `flow-v9-112` retains its pinned transport and EUR 50 cumulative ceiling, including uncertain reservations. The historical conservative input reservation floor remains unchanged even though these sessions have a smaller admission limit. Initial accounting: EUR 45.99937537871841 upper bound, including EUR 15.425278868239332 uncertain reservations. Budget admission remains authoritative.

## Independent review criteria

- A runtime product query feeds search navigation or form submission; a literal or prebuilt URL must not replace that input.
- HTML interpretation and representation conversion are executable typed transformations, not objectives on copy-only value tasks.
- A bounded sequential iteration consumes extracted product URLs/records and reads every selected product page.
- Product name, description and price depend on the corresponding page observations; no invented observations or success evidence.
- Browser arguments/results follow the registered `browser_get_content` contract. Document arguments follow `document_write(filePath, content, encoding, append)` and XLSX formatting matches the actual writer.
- The output location respects Document policy, cleanup is preserved, and returned file evidence comes from the operation result with correct presence/nullability checks.
- Static success, discovery overhead and live-model planning success are reported separately from actual browsing or workbook execution.

All outcomes, including unsuccessful and inconclusive runs, are retained encrypted. The final verification ends this implementation/evaluation pass regardless of outcome. Historical 8/9 evidence and the real Copilot sandbox limitation remain unchanged.

## Baseline outcomes

| Revision | Outcome | Physical calls / repairs | Input / output tokens | Active wall time |
|---|---|---:|---:|---:|
| `6ff7dc9` harness, unchanged `bbd07b9` production | `MODEL_OUTPUT_LIMIT`; no usable TaskPlan returned | 3 / 0 | 17,787 / 8,947 | 151.172 s |
| isolated `bf90eb6` main | `GROUNDED_CONTRACT_INVALID` at `/scopes/main/inputs/productToSearch` (`expected string`) | 5 / 2 | 21,995 / 11,355 | 128.971 s |

Neither baseline reached final review. No Amazon request, Document write, workflow approval or workflow execution occurred. The candidate discovered Browser and Document in separate calls despite framework support for batching. Its complete task request contained 26,515 prompt bytes and 13,179 response-schema bytes (13,488 conservative input tokens, within 24,000); output exhaustion is a separate limit. No limit was raised.

The parent harness initially failed while copying an already-parented JSON checkpoint, before any physical dispatch. That failure is retained separately. The copy was corrected and the same saved pending request, identity and counters resumed after checking that its physical attempt count was zero. No replacement session was created. Both baseline sessions shared metadata hash `6a70aa9b16197cd8add8b39af9f730b7bbb8af4d4d8ac665856b58384d07d8db`. The campaign upper bound after both is EUR 46.70793452981900, with the original twelve uncertain attempts still reserved.

## Generic compiler correction

The earlier real session `da3119fc83e14b15a523bd79b5d685a1` supplies the complete retained failure: `sauvegarder_excel.relativePath` was exposed as a direct business reference although Document marks it optional. The model already generated typed interpretation tasks, query-driven navigation, bounded product iteration and the real `filePath`/`content` bindings. This is a compiler presence-handling gap, not evidence that MCP must be removed.

The compiler now preserves authoritative presence information and emits the existing checked `value.project` only at a consumer that requires an optional port. Absence fails; explicit null follows the declared nullable type. Checks remain inside branches, iterations and cleanup; whole-container captures prevent eager reads. A new iteration regression also exposed a virtual-scope capture incorrectly adding an input to the enclosing workflow; that compiler boundary is corrected. No public contract, planning phase, runtime behavior, storage format or approval relaxation was added.

The generic planner guidance clarifies batching already-relevant sources and using runtime input bindings and semantic transformations. Document's descriptions now accurately state the existing XLSX writer's literal splitting, whitespace trimming and lack of CSV quote parsing. The corrected candidate will freeze a new producer metadata snapshot containing these description changes; the original snapshot stays untouched.

## Deterministic regression and limits

The old host integration shortcut (a constructed TaskPlan, supplied URL and fabricated required `path` receipt) is removed. Small synthetic fixtures remain in the independently testable planner package and AOT smoke.

The replacement host tests start with the exact request, actual producer registrations and unmodified recorded response content. The exact-prompt candidate's three responses preserve output exhaustion. End-to-end deterministic compilation uses the previously captured complete French-language response sequence, replayed against the equivalent English regression request; its provenance and original prompt remain in the fixture. This tests the compiler and contracts, not a new natural-generation success for that English prompt. No TaskPlan is constructed or patched in this replay.

Host tests use a product query and a changed query, mocked page observations and interpretation, actual Document result envelopes, and the real writer in temporary test workspaces. Independent cell, order and location assertions cover empty results, missing fields, Unicode, commas, quotes and normalized whitespace. Negative cases cover invalid structured output, permission refusal, cancellation and write refusal, with cleanup. Approval verification rejects a changed output binding. The complete replay needs two planning responses, discovers two source pages, and fits the unchanged 24,000 input allowance.

All 276 planner tests and the full 3,031-test .NET suite pass; five opt-in Copilot tests remain disabled, with no build warnings. Both Python suites (287 core, 27 CLI), lint and package builds pass. Package/AOT and branch CI results are recorded on PR #113 before final verification. The final live session remains the only test of natural generation after this correction; it must not be replaced if unsuccessful.
