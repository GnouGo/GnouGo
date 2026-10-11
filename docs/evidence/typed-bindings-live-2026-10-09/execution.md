# First approved live execution: failed candidate eligibility

The user approved the [revision-9 artifact](amazon-review.md) and its five
requirements with “I approve , please live test :)”. The existing approval command
was submitted once, advancing the session to approved revision 10 without changing
the artifact. The run `typedbindings20261009b-amazon-1` then executed once on frozen
candidate `7a76fc95`. No older or uncertain invocation was resumed.

**Execution failed; the independent E2E oracle failed. No products were visited
and no Excel workbook was created. Browser cleanup succeeded.**

## Observed sequence and exact cause

1. Document policy read succeeded.
2. Browser navigated to the query URL and returned a complete snapshot containing
   **51 pages / 1,500 records**.
3. Independent mapping generated one script from three complete examples, then
   processed and validated **all 51 pages**, including the 48 omitted from examples.
   It produced 5,562 output bytes without resource exhaustion or specialization.
4. The compact gate view offered one alleged consent candidate: the footer's
   **“Cookies” information hyperlink**, with authoritative actions **`follow`,
   `press`**. The mapping selected it by label, without requiring compatibility
   with the downstream `activate` action.
5. Interpretation received those actions, nevertheless returned
   `hasConsentCandidate: true`, and selected that observed candidate ID. Both
   lookups correctly reconnected the exact original record. No selector or
   reference was fabricated.
6. The conditional attempted `browser_click(requestedAction: activate)` on that
   reference. Browser returned **`ACTION_MISMATCH`**, normalized as
   **`MCP_CALL_ERROR`**, before interaction. A hyperlink supporting `follow` cannot
   satisfy control activation.
7. Workflow finalization closed Browser. The independent cleanup probe confirmed
   no active page; its extra idempotent close is distinct from the workflow's close.

[Selected observation, compact request and model decision](selected-action.json),
[producer error](execution-result.json), [event summary](execution-events-summary.json)
and [durable invocation states](execution-journal-summary.json) retain the chain.
The full original snapshot and receipts remain in encrypted storage.

The failure is **business-candidate eligibility**, not oversized interpretation,
broken lookup, mapping resource exhaustion, missing receipts or a Browser defect.
The compact interpretation's verified input was only **272 tokens**. Shape and
source-grounding validation did not establish that a cookies-labelled link was a
consent control. A declared boolean did not establish action compatibility either.

## Minimal generic correction proposed

Use the existing candidate-view/data-adaptation stage to admit only observed
candidates compatible with the requested operation/action **before** business
selection. Select IDs only from that eligible view, then retain both lookups to
original records. An optional action with no eligible observed candidate must not
be enabled merely because an interpretation declares `hasCandidate: true`.

Keep the producer's final actionability, identity and expiry checks unchanged.
Action compatibility is necessary but still does not establish the business meaning
of a control. This calls for correcting the explicit extraction/selection contract
and composition, not changing Browser to activate links, adding site rules,
restoring a conditional-proof engine or introducing another executor/operator.

The generated script also contains per-field `slice` bounds. They are retained in
the receipt, not presented as evidence that all potentially relevant business facts
were selected. Every source page was validated, but that alone cannot prove semantic
completeness. The observed stopping cause remains the one incompatible candidate.

No production correction or new paid attempt was made in this execution turn.
A future revised artifact needs a fresh run identity and its own concrete review;
this already-started run cannot be reset or replayed.

## Separate results and costs

| Stage | Result |
| --- | --- |
| Planning | Passed after three explicit review revisions; 6 logical requests / 7 physical attempts, 0 automatic repairs, 2 discovery reads, 652,335 ms; €0.911958. |
| Execution | Failed after 71,581 ms; 3 logical/physical inference calls: URL construction, one mapping generation, gate interpretation. 35,377 verified input / 2,720 output tokens; **€0.229377**. |
| Independent E2E oracle | **Failed:** `workflow_execution_failed`, `workbook_missing`. No XLSX cell or product-visit success is claimed. |
| Cleanup/recovery | 17 normal steps and 1 finalization step. Journal status `failed`, finalization complete. Rejected MCP call has verified external completion; no new unknown inference or reconciliation state. |

Mapping's complete request estimate was 47,781 tokens, below 96,000; provider usage
was 34,979 input tokens. The maximum observed sandbox counters were 4,654/10,000
statements, 66.443/5,000 ms, 17,190,696/50,000,000 allocated bytes and
5,807,938/50,000,000 materialized bytes. These are distinct counters, not an increased
allowance. No cache hit, program repair or specialization occurred. The telemetry's
12 `failure_groups` is retained as emitted; it does not mean 12 failed provider calls.

[Accounting](execution-accounting.json): **€117.920973 / €150**, including the
unchanged **€6.501615** historical unknown reservations. Approximately **€32.08**
remains. Run planning plus execution cost **€1.141335**. No limit or permission changed.

## Validation and delivery limits

- Frozen source, binary and oracle hashes matched before execution; approved YAML
  and accepted requirements still match after execution.
- Configured pricing/currency readiness passed. All historical uncertain logical
  requests remain explicitly retained inconclusive; none was reclassified or freed.
- Existing `BrowserActionReferenceTests` reran locally: **25 passed**, including
  rejecting control activation of a link with zero interaction and allowing explicit
  `follow`. No assertion or timeout changed.
- CI's planner-validation job failed in the unrelated Copilot cancellation fixture's
  teardown: `IOException: Directory not empty`, `CopilotAttachmentTests.cs:214`.
  Its Browser (136), Document (64), Planning (1,198), integrations (99) and persistence
  (17) tests passed. The CI failure is retained and is not reported as green.
- No code-review evaluation, cohort expansion, extra live execution, production
  source change or architectural layer was added. PR #117 remains draft.
