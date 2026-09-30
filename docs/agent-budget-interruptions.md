# Agent budget stops and reconciliation

The retained `CodeReviewTest` run `625232f39685e1a6b16e30ada70ebcba` stopped
with four admitted requests and 167,402 conservatively charged tokens against
200,000. Its next input reservation did not fit. Neither the 20-call nor the
30-minute ceiling was reached. Charged tokens are non-refundable upper bounds,
not measured provider usage. Planner token settings cannot extend an approved
agent task's execution budget.

[Sanitized original receipts](../tests/GnOuGo.Agent.Server.Tests/Fixtures/AgentInterruption/retained-execution.json)
retain the unresolved adapter outcome and the misleading parent invocation error.
The originals were read through public KeyVault APIs and verified unchanged. Their
missing cessation evidence is not repaired retrospectively.

## Correction

- The integration records typed call/token/deadline admission reasons and preserves
  SDK tool observations when a bounded send throws. Public messages omit provider
  exception bodies, prompts and assistant claims.
- Budget refusal closes further inference and tool admissions. Host file handlers
  acquire operation leases at actual entry; stopping waits for admitted handlers
  and reservation persistence. SDK pre/post admissions are checked against observed
  tool calls, including commands admitted before their execution event arrives.
- Only matching, complete observations plus successful managed-session shutdown
  permit a terminal `budget_exhausted` receipt with `AGENT_BUDGET_EXHAUSTED` details.
  Shell/background commands still require authoritative exit observations. A
  transport failure, missing/conflicting event, shutdown failure or ten-second stop
  timeout remains unresolved. Abort alone proves nothing about cessation.
- Interruption observations are encrypted before shutdown. A crash or failure
  saving the terminal receipt cannot authorize cleanup or another dispatch. A
  retained admission reason can be displayed without claiming completion.
- Flow keeps diagnostic observations separate from external completion. Nested
  finalization preserves the original leaf cause and lists actual unresolved
  invocation IDs. Cleanup is blocked until all relevant work is resolved.
- Reconciliation re-inspects uncertain adapter observations; verified receipts are
  reused without repeating work. Explicit operator-confirmed stops remain audited
  failures. Historical receipts do not restore internal Copilot sessions.

The execution page shows safe causes, approved ceilings, charged reservations and
cessation status. Read-only inspection dispatches neither tools nor inference;
reconciliation remains an explicit, revision-checked action.

TaskPlan, approvals, conservative accounting, permissions, planning format 10 and
execution journal schema 9 are unchanged. There is no new planning phase or
provider-specific Flow behavior. Larger task budgets require explicit revision
and approval; this correction does not guarantee completion within existing limits.

## Validation and deployment

Deterministic regressions cover retained-counter refusal, delayed and late file
operations, SDK admission races, open/conflicting command observations, transport
interruption, shutdown failure/timeout, terminal-receipt loss, nested recovery,
primary/cleanup error separation, tenant isolation and read-only UI rendering.
The existing planner/compiler lifecycle fixture adds a confirmed budget stop with
mocked agent effects and real temporary Git/Cmd cleanup; unrelated files stay intact.

Exact suite, package, Native AOT and platform CI results are recorded on
[draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). Local validation evidence,
including the two initially failing reporting regressions, is retained under
`artifacts/agent-interruption-2026-09-29/`.

Deploy runtime and Copilot MCP together. Preserve the stopped run and clone;
inspect its existing receipt before any explicit reconciliation. Do not mark it
stopped merely because it exhausted its budget. No machine policy changes,
permission broadening, paid inference, real Copilot task, external workflow or
automatic cleanup of the retained run is part of this correction. Dependency
download restrictions remain. Keep the PR draft and do not merge.
