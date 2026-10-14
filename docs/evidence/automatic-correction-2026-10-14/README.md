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
arguments, relative order, control boundaries, finalizers and ordered guards.
Independent tasks remain unchanged. A malformed field may be repaired to an
actually declared field on the same producer; punctuation is never normalized.
Replacing an assertion with `true`, adding external work, or moving an action into
a branch requires explicit review. Ordinary pre-existing producer-chain patch
slots retain their original authority and independent validation.

Known runtime composition failures create one unapproved draft per tenant,
failed run and artifact. The draft restores the saved TaskPlan and accepted
requirements. No workflow save or execution occurs. Unknown failures, budget,
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
and disposable stores. They do not alter those sessions or their verdicts.

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
