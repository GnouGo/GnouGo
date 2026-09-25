# Designer input-budget diagnosis and correction

## Retained observation

The reported Designer session completed two successful provider requests and stopped
at the tasks phase before dispatching a third. It had discovered two sources with
nine and four operations. No TaskPlan had yet been produced and no repair had run.
This is a local `MODEL_INPUT_LIMIT` admission failure, separate from the preceding
[HTTP 400 diagnosis](planning-http400-portability-2026-09-25.md).

Read-only inspection of the pre-stop revision found 28,788 UTF-8 prompt bytes,
including about 23.3 KB of operation metadata, plus 11,282 response-schema bytes.
The unchanged conservative estimator, `(promptBytes + schemaBytes + 2) / 3 + 256`
with integer division, yields 13,613 input tokens against the saved 12,000 limit.
The preceding successful request was estimated at 11,473 tokens; its receipt reports
7,047 input tokens. These estimates are admission bounds, not measured billing.

Removing all port descriptions would reduce this particular estimate to 11,995,
leaving almost no margin and removing useful authoritative metadata. No descriptions,
contracts, schema constraints or request content are removed by this correction.
No provider-specific tokenizer adjustment is inferred from these two observations.

## Correction and recovery

Implementation commit `abd5270` raises only the default for **new Agent.Server
Designer sessions** to 24,000 input tokens. Explicit host overrides and existing
sessions' saved settings remain authoritative. Standalone Flow defaults stay at
12,000; output, call, repair, spending and elapsed-time ceilings are unchanged.

The input-limit diagnostic now describes the complete-request estimate, local
pre-dispatch stop and explicit continuation, retaining its planning-phase location.
Designer expands generation settings for this stop and labels its action
**Apply settings and resume planning**. The existing revision-checked command is
reused; no new command, DTO, model phase, planning representation or storage format
is introduced. Opening the page does not change limits or resume the session.

For the reported session, 16,384 admits the currently blocked request and 24,000
provides more headroom. The operator can change **Generation settings → Input token
limit** and explicitly apply it to continue the same session. There is no pending
request to reconcile. Later discovery can still exceed the new allowance, and
subsequent model requests may incur usage. This change makes no claim that the
complete requested workflow will plan or execute successfully.

## Deterministic validation and limits

- The sanitized two-source, thirteen-operation regression reproduced the local stop
  after two model calls before the production change. Its diagnostic location was
  incorrectly `/`; the new test now passes with `/phases/tasks` and actionable text.
- Regression tests verify explicit continuation after serialization/recovery,
  unchanged discovery receipts and accounting, retained request identities, a new
  third-call identity, unchanged standalone defaults and explicit host overrides.
- Oversized requests still stop at either selected ceiling. Pending requests and
  stale revisions reject settings changes without modifying the baseline.
- Designer tests verify expanded settings, the explicit resume action and read-only
  inspection of the stopped session. Persisted limits are not silently replaced.
- All **236 planner tests** and **410 Agent.Server tests** passed with warnings
  treated as errors. Local documentation links and whitespace checks passed.
- Initial failures and successful logs remain under
  `artifacts/planning-input-budget-2026-09-25/`. An initial test used reference equality
  for deserialized page lists; it was corrected to compare the complete serialized
  discovery state. Production persistence was unchanged.

Final-revision branch CI results are recorded in [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).
The original encrypted session, requests, receipts and historical evidence were
left untouched. No diagnostic inference, paid/live benchmark or real Copilot task
was run. Historical live correctness remains 8/9; real Copilot command edit/test
execution remains unverified under the existing mandatory sandbox policy. Keep the
PR draft and do not merge.
