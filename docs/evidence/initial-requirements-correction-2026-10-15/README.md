# Initial requirements correction

Baseline: `f5a7a3e`. This change is offline. No provider calls, live execution,
historical session advancement, approval or financial mutation is included.

## Defect and correction

The committed responses for `9d073e284c5142a0923d3f8dd99b6966` and
`b992404c2ada42f2916ae2aa2232f642` contain a plan, `requirements: null`, and no
clarification. Both satisfy their original strict structured-output schemas.
The semantic requirement check correctly rejects them. Before this change,
the automatic-correction router excluded that initial semantic failure.

With the captured policy enabled, an initial requirements/interface defect
now uses the existing single structural regeneration, before any requirements,
plan or baseline have been accepted. The original request, discovery receipts
and precise diagnostic reach that corrective request. The same cumulative
calls, tokens, elapsed time and replan allowance apply. A second invalid
proposal stops; a real ambiguity may return clarification. Approval remains
separate.

`REQUIREMENTS_INVALID` now identifies absence, a blank summary, an empty outcome
list, a blank identifier/description, or a duplicate identifier at its exact
location. Structured-schema failures still take precedence where applicable.
Fresh initial prompts and annotation-only schema hints distinguish discovery,
clarification and the first plan. Requests with accepted requirements retain
their previous presentation, avoiding redundant initial guidance in their
request allowance. Private repair templates and already-issued requests do not
change.

## Separate proofs

- **Original receipt replay:** `replay.cs` reads encrypted records only through
  public KeyVault APIs. It verifies ownership, exact issued request identity,
  original response-schema acceptance and unchanged historical record bytes.
  A memory-only runtime applies each committed receipt, then intercepts one
  corrective request and returns a deterministic clarification. No provider or
  MCP is reachable. This proves routing and receipt preservation, not the
  business correctness of either retained plan.
- **Planning:** generic fixtures return a complete corrected proposal, reach
  review after two deterministic calls and one replan, and reproduce identical
  v15 YAML. Tests cover semantic/schema defects, legitimate discovery and
  clarification, repeated failures, exhausted allowances, a retained baseline,
  uncertain requests, omitted policy and committed-receipt recovery.
- **Agent.Server:** the normal background owner and encrypted store reach
  review or a bounded stop. Polling and restart do not dispatch another call;
  cumulative usage remains two calls/30 synthetic tokens. No automatic approval
  or save occurs, and another tenant cannot read the session.
- **Execution:** the existing real local Browser/Document composition runs
  after deterministic correction. Workbook cells are compared independently
  with original local observations, including repeated selection order,
  missing cells, exact text, writer path and Browser closure. Typed copying,
  status checks and row assembly require no inference.

## Recorded results at the evidence checkpoint

| Gate | Result |
| --- | --- |
| Focused baseline plus first regressions | 99 passed |
| Expanded new routing/recovery regressions | 19 passed; the subsequent full suite includes all 22 new cases |
| Complete Planning suite | 1,397 passed, zero failures, `-warnaserror` |
| Normal host owner + local workbook scenarios | 10 passed (two host cases and eight local scenarios) |
| Retained `9d073e…` receipt | Original schema valid; 1→2 calls, 0→1 replan; clarification; corrective estimate 8,596/24,000 |
| Retained `b99240…` receipt | Original schema valid; 3→4 calls, 0→1 replan; clarification; corrective estimate 21,472/24,000 |
| Exported workbook inspection | Two workbooks; each has 10 visits, 39 complete pages and 11 rows including the header; exact observed cells/order and cleanup pass |
| Skill validation | Passed |

The full serial solution, Release/package, Native AOT, published encrypted
recovery and candidate CI checks follow this checkpoint. Their final results
are reported on PR #117, tied to the candidate commit. Historical CI success is
not substituted for candidate validation.

## Validation and limitations

The first six regression cases failed before the implementation. Broader
validation exposed a schema-annotation assumption and a request-size overrun;
the final presentation keeps one factored hint and scopes the initial guidance
to requests without accepted requirements. No test assertion or ceiling was
weakened. Failed logs are retained alongside final validation evidence.

The resulting evidence establishes one useful bounded correction opportunity.
Deterministic responses cannot demonstrate future model compliance or live
business acceptance. Neither stopped historical session is reopened. Compiler
v15, handwritten YAML, runtime behavior, permissions, receipt semantics and
resource ceilings remain unchanged. PR #117 remains draft.

To reproduce the read-only receipt probe, compile `replay.cs` in a disposable
net10.0 console project referencing `GnOuGo.Flow.Planning` and
`GnOuGo.KeyVault.Core`, then pass the existing workspace directory as its sole
argument. It has no storage write or provider/MCP implementation. The public
repository retains only hashes and measurements, not decrypted requests or
page content.

The [retained replay report](retained-replay.json) records original hashes.
[Workbook results](workbooks/independent-inspection.json) are reproducible with
`python3 inspect-workbooks.py workbooks`. The [composition artifacts](composition/)
contain the incomplete proposal, corrected TaskPlan, actual v15 YAML and
counted request estimates. Their scope is the generic local fixture.
