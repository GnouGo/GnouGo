# Server PR #597 retry with a 64,000-token allowance

## Result

**The input-limit blocker was removed, but real execution remains blocked by a
provider/transport failure.** Server chat completed interpretation and dispatched
the first bounded repair. That repair returned HTTP 500 after approximately 301
seconds, without a completion/usage receipt. The planner stopped with
`MODEL_DISPATCH_UNVERIFIABLE`, preserving the pending request identity.

No artifact reached FinalReview, no workflow was approved, and no repository
clone, command, test, Copilot review, evaluation or publication occurred. No second
PR or replacement session was submitted after the uncertain dispatch.

This is a configuration-only follow-up to the
[12,000-token Server attempt](planning-server-real-e2e-2026-09-20.md).
Source head was `dbe4243`; the published production binaries remained `fd316fc`.
There were no production source, architecture, schema, prompt or retry-policy
changes.

## Configuration and offline preflight

Only the stored E2E agent `desktop-planning-e2e` was updated through public
`agent_update`: `generator.max_input_tokens` changed from **12,000 to 64,000**.
Its generic `workflow.plan → workflow.execute` structure is unchanged. The update
was verified by rereading the stored agent. Other agents and repository defaults
were not changed. The new test-agent setting is retained for future validation.

| Bootstrap | SHA-256 |
| --- | --- |
| Before | `966478c847defc9afed16b92f4023ff098174017187e553d232d04068d134ed7` |
| After | `d88dcf17190269cd15f5cb654d90322f29ba1966ce431e40a152a49bcfb896e7` |

Unchanged settings: eight calls, two repairs, medium reasoning, 32,768 output
tokens, existing total-token/time limits and the EUR 20 best-effort target.
The provider-cap waiver and unknown-usage reporting remain in effect.

Before dispatch, a public-KeyVault, in-memory replay of the previous completed
receipt rebuilt its repair context with the proposed allowance. The increased
shortlist grew that request from 48,483 to **53,211 estimated input tokens**, still
within 64,000. No evidence was removed, no provider was called, and no stored
session or accounting was changed. Hashes and timestamps of the old session,
request, receipt and budget also remained identical after the new live attempt.

## Actual Server attempt

The isolated published Release Server and Chromium chat were restarted with
background planning disabled and the existing dedicated telemetry/index paths.
The unrelated host was left untouched. The UI selected the existing agent and
submitted the same read-only request in a fresh chat. No direct planning API or
approval bypass was used.

GitHub MCP confirmed [PR #597](https://github.com/AxaFrance/SmartGuide/pull/597)
remained open, non-draft and unmerged, with unchanged head
`0b7899b1944aaab5b742e12c6238825155fa2bac` and base
`667b097d205f7a376e7c8fc09f1f88e08a4dc5c6`.

| Field | Value |
| --- | --- |
| Submission | 2026-09-20 16:56:30.038 UTC |
| Stopped state | 2026-09-20 17:05:30.081 UTC, revision 3 |
| Session | `37edc1f29c97c5cd5596df52662e30e956bbc69aab8bb25be31de483ab02b6e5` |
| Chat correlation | `f11b7fa3999636ecf580f58bf774cdae` |
| Prompt SHA-256 | `6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649` |
| Provider/model | OpenAi / `gpt-5.5-2026-04-24` |
| Reserved calls / repair attempts | 2 / 1 |
| Completed model receipts | 1 |
| FinalReview / approval / scenarios | None / none / none |
| Total planning duration | Approximately 540 seconds |

The initial request exposed 24 capability cards instead of 16. Its complete
estimated size was 14,154 input tokens. The resulting proposal had 196 blocking
diagnostics, including unresolved capability selections, invalid bindings and
undeclared computation helpers. This is an **invalid model-authored intent**;
no builder defect or safety violation was established.

The planner issued 30 typed correction targets. The actual repair request included
167,762 prompt bytes and 12,939 response-schema bytes, for **60,490 estimated input
tokens**. It passed preflight under the new limit without removing diagnostics or
contracts.

## Provider failure and accounting

The original interpretation reservation completed after approximately 236 seconds:

```text
37edc1f29c97c5cd5596df52662e30e956bbc69aab8bb25be31de483ab02b6e5:1:0252c5fae7a8ccd5e874b1101d2eb5e43f14e0d45cf35bdcd1d33d275694e9fe
```

Its receipt records **11,007 input tokens and 11,627 output tokens**; 3,583 reasoning
tokens are included in the output total. The response schema hash remains
`2e2c9feb316bbade87303fea3c3c86d915f8bb3e581c73fa031117721bdc4dfd`.

The repair was reserved at 17:00:28.982 UTC under a new identity:

```text
37edc1f29c97c5cd5596df52662e30e956bbc69aab8bb25be31de483ab02b6e5:2:dfe4a8fbafc94399e4d5011bd5fb066dd33488b00e27ebbce1b23ea511752703
```

Its original schema hash is
`6d07a8561f5252225e3a03a026af65f0c7febf917fc893cafc4e40b0c14050df`.
The transport log reports `StatusCode=500`, `AttemptCount=1` and
`RetryExhausted=True`. Elapsed wall-clock time was approximately 301 seconds.
This establishes a **provider/transport failure**, not a confirmed local timeout;
the upstream cause is unknown.

No completion receipt exists for that repair. Its input/output usage and monetary
cost remain **unknown, not zero**. The pending request, two reserved calls and
cumulative known token usage remain stored. No automatic resend, new identity or
replacement session was used to bypass this state. The normal designer-revision
offer was skipped through the UI.

Across the two real Server attempts, the completed receipts establish 19,779 input
and 22,066 output tokens, plus this one uncertain repair dispatch. Monetary cost
remains unavailable; the estimator's stored zero is not treated as a free call.
Prior Desktop sessions, benchmark ledgers and accounting were not reset. No
Copilot inference was dispatched.

## Verification and remaining gate

Eleven existing focused regressions passed in Release with `-warnaserror`: five
cover input-limit preflight/reconfiguration, output ceilings, budgets and approval;
six cover encrypted receipt replay, restart and durable reservations.

```bash
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -c Release -m:1 --no-restore -warnaserror --filter 'FullyQualifiedName~InputBudgetPreflightDoesNotConsumeARepairAttempt|FullyQualifiedName~RepairInputPreflightDoesNotConsumeAnAttempt|FullyQualifiedName~ExplicitOutputCeilingIsPreservedDuringInterpretation|FullyQualifiedName~BudgetsAndApprovalSurviveRevisionAndRejectStaleCommands'
dotnet test tests/GnOuGo.Flow.Integrations.Tests/GnOuGo.Flow.Integrations.Tests.csproj -c Release -m:1 --no-restore -warnaserror --filter 'FullyQualifiedName~WorkflowPlanningPersistenceTests'
git diff --check
```

The preceding 1,558-test validation and warning-free Release publish cover the
unchanged production source; they were not repeated or represented as a new full
suite. Temporary inspection tools built without warnings. All new real execution
evidence is limited to Server planning, public agent configuration and read-only
GitHub preflight.

No review workspace was created, so no repository cleanup was required. The
isolated host/browser were stopped after the failed chat ended; encrypted evidence
and telemetry were retained, and the unrelated host remained running. No SmartGuide
source changes, source pushes, metadata changes, merges, deployments or review
publication occurred.

The configuration experiment confirms that the existing repair path can dispatch
within 64,000 tokens. It does **not** establish repair success or E2E reliability.
Further progress requires investigating the upstream HTTP 500 and reconciling the
uncertain dispatch through a legitimate recovery path. The failed request identity
must not be resent or its accounting discarded. No planner change is justified by
this provider failure.
