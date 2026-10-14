# Bounded correction using existing planning authority

Baseline: `08393a60807a49a23e3bda5c33cbe726d57371b6`, PR #117 (draft).
No provider calls, external live executions, historical-session commands or
financial changes were made for this correction.

## Behavior change

New Agent.Server sessions capture `TypedWorkflowPlanning.AutomaticCorrectionEnabled`
(default true). Sessions without that option retain their existing behavior.
Compiler v15, YAML, executors, schema acceptance, permissions, mapping repair and
execution allowances are unchanged.

The existing advancement loop now chooses an ordinary issued patch, a distinct
scoped revision for protected declarations, or one structural regeneration.
All consume the original replan/call/token/time counters. A pending receipt is
processed under its original request before any new decision. A changed diagnostic
message alone cannot justify another targeted attempt: unresolved findings after
that attempt escalate once, then stop. Candidate fingerprints also detect repeats.
Progress is bounded private data in existing session options, not a new journal.

The structural comparison preserves accepted interfaces, choices, operations,
arguments, relative order, explicit scheduling dependencies, control boundaries,
finalizers and ordered guards.
Independent tasks remain unchanged. A malformed field may be repaired to an
actually declared field on the same producer; punctuation is never normalized.
Replacing an assertion with `true`, adding external work, or moving an action into
a branch requires explicit review. Ordinary pre-existing producer-chain patch
slots retain their original authority and independent validation.

Known runtime composition failures create one unapproved draft per tenant,
failed run and artifact. The draft restores the saved TaskPlan and accepted
requirements and saved policy. No workflow save or execution occurs. Unknown failures, budget,
permission, admission, transport and uncertain completion are excluded. Authored
YAML without recoverable intent pauses for a Designer clarification rather than
being imported or automatically rewritten.

## Deterministic reproductions

| Original defect | Correction route and preservation | Expected call accounting |
| --- | --- | --- |
| `available},{` / Unicode malformed field | Targeted `requires` revision; other operand and `and` ordering unchanged | 1 initial + 1 correction, 1 replan |
| `choice(task)` | Existing typed reference patch; no new business choice | 1 initial + 1 correction, 1 replan |
| Invalid `each.input` and assembled result | Targeted producer `each`/inputs/resultType paths, not unrelated consumer retries | 1 initial + 1 correction, 1 replan |
| Missing typed producer | Structural regeneration within accepted requirements | 1 initial + 1 correction, 1 replan |
| Empty patch | One structural escalation, no repeated patch | 1 initial + 2 corrections, 2 replans |
| Guard replaced by `true` | Reject targeted and structural proposals; retain baseline | 3 calls total, stopped |
| Unrequested operation, branch movement, independent edit | Reject complete proposal atomically | 3 calls total, stopped |
| `status.success` without source grounding | Separate facts from deterministic producer outcome in a new draft | 1 deterministic revision, 1 replan; 0 status-inference calls |
| Budget/configuration/unknown failure | Stop without correction | 0 corrective calls |
| Interrupted correction with committed response | Reuse exact issued request/schema at exhausted call limit | 0 redispatch, counters remain 2 calls / 1 replan |

The failures correspond to retained Agent.Server investigations in
[generated guards](../generated-guards-2026-10-14/README.md),
[mapping status](../mapping-status-isolation-2026-10-14/README.md) and earlier
Designer composition evidence. The new fixtures use arbitrary task/field names
and disposable stores. A separate replay uses the existing sanitized retained
proposal containing both malformed guards and changes only those two field
references. It intentionally does not claim to correct undiagnosed business
omissions in that proposal. They do not alter those sessions or their verdicts.

## Functional proof and limitations

Local tests run real Flow, Browser and Document components with deterministic
inference and loopback networking. The independent oracle inspects visits,
action arguments, exact descriptions, XLSX headers/cells/blank positions/order,
writer path and Browser closure. Cases include complete acquisition, missing
facts, repeated IDs, unknown IDs, incomplete acquisition and denied actions.
Restart reuses committed results without repeated navigation, inference or writes.

`status.success` cases include producer failure, absent snapshot and both capture
and manifest truncation. These stop before extraction; they do not produce a
successful empty record. The fabricated-boolean rejection remains intact.

These results establish bounded correction and exact deterministic execution.
They do not show that a future model will choose the correct patch, detect every
business omission in a valid plan, or preserve arbitrary semantic restructurings.
Ambiguous changes stop for explicit review; newly needed external work is not
inferred from prose. Live business acceptance remains unproved.

Validation results and actual paired fixture artifacts are recorded alongside
this report. Failed development runs are retained with their causes; they are
not counted as successful acceptance.

## Final local results

- Serial solution: **5,692 passed, zero failures, 13 existing skips**, across
  33 assemblies with `-m:1 -warnaserror`.
- Final Planning refresh: **1,370 passed**. Final host/retained/local refresh:
  **28 passed**, including the last added retained two-guard replay.
- The full host group passed **1,004 tests** (one existing skip), including
  **203 local composition fixture cases**. The focused refresh covers the new
  retained test added after that host assembly had been built.
- Four independently packed Release libraries (Core, Planning, Integrations,
  Persistence), Planning Native AOT publish/smoke, trimmed self-contained
  Agent.Server publish, published encrypted recovery and skill validation all
  passed with no build warnings. The server uses its supported trimmed profile;
  Planning smoke uses Native AOT. Full commands and logs are in
  [published-checks.json](validation/published-checks.json).
- Candidate CI results are linked from the PR delivery comment. No test timeout,
  assertion or allowance was weakened.

### Actual before/after artifacts

| Fixture | Exact change | Route | Full request estimates: initial / correction | Result |
| --- | --- | --- | --- | --- |
| [Retained two guards](fixtures/retained_guard-diff.json) | Two `available},{` ports → declared `available`; all other JSON unchanged | Two exact `requires` paths | 22,128 / 8,040 | Review, 2 calls / 1 correction; 0 external dispatches |
| [Generic guard](fixtures/guard-diff.json) | One `success},{` port → declared `success` | Protected `requires` revision | 22,190 / 6,706 | Review, 2 calls / 1 correction |
| [Generic choice](fixtures/choice-diff.json) | `choice` → typed `output`; producer/port unchanged | Existing ordinary patch | 22,190 / 4,694 | Review, 2 calls / 1 correction |

The [fixture directory](fixtures/) contains each complete before proposal, after
TaskPlan, actual v15 YAML, scope/progress record and exact JSON diff. Request
figures use the existing admission estimator, not billed provider tokens.
Deterministic responses prove expressibility and execution; they do not measure
future model compliance. No artifact in these planning replays is auto-approved.

### Runtime and workbook outcomes

| Local case | Runtime | Independent result | Deterministic runtime calls / mappings |
| --- | --- | --- | --- |
| Complete | Success | Ten visits, exact workbook and cleanup; production oracle passes | 23 / 11 |
| Repeated selection | Success | Ten visit occurrences, repeated row preserved; production oracle passes | 23 / 11 |
| Missing permitted facts | Success | Exact known fixture blanks and unchanged other cells; production oracle retains its conservative absence rejection | 23 / 11 |
| Unknown selected ID | Expected failure | Strict lookup fails before product visits/writing | 3 / 1 |
| Incomplete acquisition | Expected failure | Fails before extraction or writing | 1 / 0 |
| Denied writer destination | Expected failure | No workbook or out-of-workspace file is written | 23 / 11 |

Successful fixtures process **39 complete pages**, offer **12 candidates**, and
visit ten occurrences. Each workbook has the supplied header and ten rows.
Typed copying, status/row assembly and ID reconnection use **zero inference**.
The global decision request is **2,690 bytes**. Completed encrypted recovery
repeats **zero inference calls and zero external calls**.

The [archived XLSX files and observations](workbooks/) have a second, standalone
ZIP/XML inspection using Python's standard library. It compares cells with the
corresponding original local-page records, verifies repeated occurrences and
blank positions, rejects formulas, and checks the writer result and closure.
Run `python3 workbooks/inspect-workbooks.py` from this directory to reproduce it.
This fixture-ground-truth check is separate from the unchanged
[production-oracle verdicts](workbooks/production-oracle-results.json): the latter
still rejects the missing-facts case. No absence rule was weakened.

The complete sample uses **143 invocations**, 142 normal steps and one cleanup,
with 1,089 existing check events. Cumulative host allocation is 2,634,691,008
bytes (not peak memory or a sandbox allowance); logical journal size is
20,819,232 bytes and measured physical store/workspace size is 13,408,100 bytes.
The corresponding measured run is 9.514 seconds and completed restart is
0.844 seconds. These are single local samples, not claimed performance gains.
Detailed measurements are in `validation/local-compact-bindings-v15-*.json`.

No compiler source changed in this correction. Historical profile, approval,
manual-YAML, schema, permission, cancellation and receipt tests remain in the
full-suite results. Failed development and CI attempts remain archived separately
in [failed-development-runs.md](validation/failed-development-runs.md).
