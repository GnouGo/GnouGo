# Bounded singleton output escalation

The normal output ceiling remains 8,192. A verified truncated singleton decision
may reserve one identical request at 16,384. The allowance belongs to its canonical
semantic identity and evidence within the owning workflow/session, across later
corrections, gates, revisions and page layouts. There is no 32k fallback and no
reasoning change. Other configured ceilings retain their existing stopping behavior.

Multi-decision truncations still partition recursively in canonical order. A
singleton inside a partition uses the same bounded escalation rule. Completed
siblings remain reusable. Output partitions and output escalation consume global
calls, tokens, cost and active time, but no semantic repair allowance. A genuine
correction retains its original charge and restricted scope; an invalid escalated
answer cannot obtain another correction or escalation by changing page layout.

## Durable proof

`OutputBudgetEscalation` is appended to the existing page-origin enum. The encrypted
page, exact request and accounting retain the parent page/request, parent request and
receipt fingerprints, canonical decision/evidence, level and effective ceiling.
Consumption is recorded when the coordinator reserves the child, before dispatch.
The parent and its generation fields are cloned from the captured request, rather
than reconstructed from context. Only the output ceiling and coordinator identity
metadata differ. Ordinary requests omit absent escalation metadata, preserving their
existing fingerprints.

Both Agent.Server and Flow runtime journals require the exact session/tenant-owned
parent request and durable `output_limit` receipt. They validate singleton membership,
request/receipt fingerprints, level, ceilings and all remaining generation fields.
Escalation metadata is a coordinator contract; it is never a model instruction.
Missing receipt evidence, changed content or foreign lineage fails closed. A completed
child receipt replays without dispatch or duplicate accounting. A reservation without
a receipt remains unverifiable. A second truncation produces `DECISION_OUTPUT_LIMIT`.

Schema-5 namespaces, runtime interfaces, declaration semantics, correction limits,
human reviews and deterministic compilation remain unchanged. Archived stopped pages
remain stopped. Missing historical origin or proof grants no escalation authority.
Progress and redacted traces expose escalation origin, lineage, ceiling and outcome
separately from semantic repairs; the designer layout is unchanged.

## Offline evidence

Historical session `7a27ef2963ae49589c9b6c4196644fe7`, revision 18, was replayed through
Agent.Server's receipt-only audit harness. One historical receipt was reused, with
zero provider dispatches. The unchanged singleton request now reserves escalation,
then stops at `REPLAY_EVIDENCE_REQUIRED` because the new request has no historical
receipt. The separate 16,384 diagnostic response was not substituted. The archived
session and budget were unchanged.

Synthetic regressions cover exact request equality; zero repair allowance;
`5 → 2+3 → 1+2` recursive partitioning followed by singleton escalation; completed
sibling reuse; reservation, receipt and acceptance restart checkpoints; shared
allowances across correction/gate/revision/page changes; correction restriction and
charges; terminal 16k truncation; custom configured ceilings; forged, stale or foreign
proof; missing receipts; cancellation; and global budget exhaustion. Both journals
verify unclamped transport output and encrypted restart. A complete synthetic planner
session escalates during construction, reaches every mandatory gate and approves the
exact artifact hash. Report tests retain unknown history and deduplicate replay.

The Native AOT smoke includes recursive partitioning, singleton escalation and
receipt reuse after encrypted runtime restart. The trimmed Agent.Server smoke checks
its EF/SQLite encrypted persistence boundary. Both use the repository's existing
documented publish exceptions; no new analyzer suppression was added.

Final offline results: **3,209 solution tests passed**, zero failed, one optional live
provider test skipped across 29 projects. All **771 planner tests** passed. The
68 focused campaign, diagnostic, journal and persistence tests passed. Solution and
harness builds, Agent frontend, four affected packages, Native AOT planning/runtime
persistence, and trimmed Agent.Server EF persistence passed without warnings under
the documented publish exceptions. See the [offline record](planner-output-budget-escalation-offline.json).

## Authorized live checkpoint

Campaign `schema5-output-budget-escalation-stage1-1` permits exactly one fresh Stage-1
session after offline validation and binary freeze. It preserves the configured model,
all-low profiles, frozen scenario/catalog/policy, 12,000 input ceiling, 9,600 dispatch
target and existing global budgets. The six execution cases are accepted, rejected,
boundary, omitted default, invalid input and null threshold. Exact behavior acceptance
precedes construction; exact artifact approval requires current validations and all
six passing independent cases. The first meaningful blocker stops this campaign;
there is no replacement session, Stage 2 or patch-and-rerun loop.
