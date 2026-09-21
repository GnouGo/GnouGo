# Server live-test follow-up

**Stopped on an uncertain provider HTTP 502. No real PR review completed.**

This records the user's requested fresh live test following
[the earlier Server attempts](planning-server-live-2026-09-21.md). These are actual
Blazor chat submissions with real configured integrations. Prior uncertain
requests were not recovered, resent, rewritten or treated as free.

## Scope and preflight

- PR: <https://github.com/AxaFrance/SmartGuide/pull/597>.
- Head: `0b7899b1944aaab5b742e12c6238825155fa2bac`.
- Base: `667b097d205f7a376e7c8fc09f1f88e08a4dc5c6`.
- The real GitHub MCP authenticated successfully. The PR remained open,
  non-draft and unmerged, with no auto-merge request. The repository automation
  reviewed in the preceding report is unchanged at this base/head.
- The real Copilot integration answered its SDK connectivity probe. This is
  connectivity evidence, not a completed Copilot inference or review.
- The stored generic `desktop-planning-e2e` agent remains
  `workflow.plan → workflow.execute`. No replacement review YAML was supplied.
- Eight calls, six repairs, medium reasoning, 64,000 input / 32,768 output for
  interpretation; typed repairs retain three-target, 12,000 input / 8,192 output
  ceilings. No technical limit or permission was raised.
- The unchanged user prompt has SHA-256
  `6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649`
  and 2,761 UTF-8 bytes.

An isolated Release Server and Chromium used the existing encrypted workspace
configuration. Planning background processing remained disabled. Reading stored
requests and receipts used public KeyVault APIs. The designer's existing
`retry_model` command does not apply to chat-created workflow sessions; no
session was copied between stores or changed to bypass that boundary.

## First attempt: retrieval miss and invalid intent

Source: `3a29c57` (production code from `f18d890`). Submitted at
08:21:13.338 UTC; stopped at 08:30:16.236 UTC, revision 9.

- Session: `f405f9a70d940515ce7280df51a2f89824e73b16cb4d1192f8209502efef96c5`.
- Correlation: `de0808a18c05e3e895dba71d304cf9fa`.
- Seven calls, six repairs, seven completion receipts; no pending request.
- 40,194 input + 19,980 output = **60,174 known tokens**.
- Active planning time: **542.5 seconds**. Monetary cost remains unknown; the
  aggregate snapshot's zero estimate is not evidence of free inference.
- No FinalReview, approval, executable workflow or scenario result.

The initial model proposal included unresolved capabilities, unavailable result
bindings, invalid templates and missing contracts. The repairs repeatedly
attempted to resolve the metadata-read operation. The authoritative catalog
contained the relevant read capability, but its four advisory cards omitted it
and included unrelated operations. Model proposals invented capability names
and ultimately replaced the metadata read with a model transformation. Neither
that transformation nor the invalid workflow establishes real metadata evidence.

Classification: **capability retrieval miss**, together with **invalid intent**
and a **semantic misunderstanding** in the attempted replacement. Deterministic
validation remained blocking. Every repair contained one target and a 6,921-byte
response schema; repair prompts ranged from 13,626 to 15,577 bytes. The first
request contained a 31,188-byte prompt and a 10,458-byte response schema. Byte
counts are not token counts.

The session's 16 encrypted session/request/receipt/budget records have digest
`ccd125880cf38fc4be4699a19d9f481fa6a1e14def66a5b3b6d10698430f9288`.
The UI designer-revision offer was skipped. No further call used this session.

## Generic correction and offline validation

Commit `708eaa3` normalizes capability retrieval scores by the square root of
the number of distinct document terms. The previous additive score rewarded
verbose descriptions for incidental matches and omitted a concise relevant tool
from bounded advice. Names, descriptions and producer metadata remain the only
retrieval sources; stable identity tie-breaking is retained.

A sanitized laboratory-sample regression failed before production changes and
passed afterward. It verifies visibility in repair advice and initial
shortlisting, retains the four-card bound, and verifies that retrieval does not
resolve or validate the invalid invocation. Empty documents and tied scores have
deterministic ordering. Existing exact-tool-name advice tests remain passing.

No permission, capability matching, schema validation, response contract,
planner phase, persistence format, approval or confirmation behavior changed.
The full catalog and deterministic choice domains remain intact. This is a
targeted retrieval correction, not proof of successful live planning.

Validation on the corrected source:

| Scope | Passed |
| --- | ---: |
| Focused retrieval/correction tests | 48 |
| Full Planning tests | 199 |
| Flow integrations | 76 |
| Agent.Server | 345 |
| Flow runtime | 853 |
| AI/HTTP retry tests | 222 |

The full affected suites contain **1,695 tests**, with no failures or skips.
Release tests ran with `-m:1 --no-restore -warnaserror`. The trimmed, self-contained
macOS ARM64 Server publish and Native AOT planner publish passed with warnings
treated as errors. The Native AOT binary passed all eight offline cases, including
independent execution assertions. These fixtures are not real review evidence.
The published Server passed the schema-7 encrypted persistence, tenant isolation
and uncertain-publication replay smoke in a separate temporary directory.
Configured frontend targets ran during publish; no frontend source changed.

Commands (publish outputs and smoke persistence directories were isolated):

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.AI.Core.Tests -c Release -m:1 --no-restore -warnaserror
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishAot=true -warnaserror
GnOuGo.Flow.Planning.Smoke
GnOuGo.Agent.Server --planning-persistence-smoke <temporary-isolated-directory>
```

## Retry on the committed correction: provider stop

The corrected commit was pushed before restarting the published Server. After
another real MCP PR state/head check, the unchanged prompt was submitted through
chat at **08:34:56.289 UTC** on `708eaa3`.

- Session: `e62218e704b52e78454485c3e91cb01f02f6251a4a3e5ffdc44fe69efb8ba5cb`.
- Correlation: `3d254b4e740a4d29f801296899d0ad27`.
- Original reservation:
  `e62218e704b52e78454485c3e91cb01f02f6251a4a3e5ffdc44fe69efb8ba5cb:1:0b7acc7c94c960db1764e27b896d4edaa8c8eaab19cbe8e53f6b16b9da34613d`.
- Reserved at 08:35:00.881 UTC; stopped at 08:37:51.830 UTC, revision 2.
- One call, zero repairs, **no completion receipt**; usage and cost are unknown.
- Diagnostic: `MODEL_DISPATCH_UNVERIFIABLE`, provider temporarily unavailable.
- Transport: **HTTP 502**, `AttemptCount=1`, `RetryExhausted=True`. Observed
  dispatch-to-stop time: **170.9 seconds**; active session time: **175.2 seconds**.
  The transport log's `ElapsedMs=0` is not the observed wall time.
- The request contained a 29,892-byte prompt and 10,458-byte response schema.
  Original schema hash:
  `2e2c9feb316bbade87303fea3c3c86d915f8bb3e581c73fa031117721bdc4dfd`.
- The initial 24-card shortlist now includes `pull_request_read`, along with
  Git clone, Cmd execution, Copilot review and review evaluation. No model
  response completed, so this establishes visibility, not planning reliability.

Classification: **provider/transport failure**. No planner changes followed
this failure. No resend, provider switch, timeout increase, manufactured receipt
or replacement session followed it. The original request and accounting remain
reserved. The three retained records have digest
`b50c5799acdd4d68a76ddf8e48927bf569e84ddac509de4897048deb217129a6`.

## Accounting, effects and remaining blocker

These two new attempts reserved **eight calls**, used **six repairs** and retained
**seven completion receipts**: **60,174 known tokens plus one uncertain call**.
Costs remain unknown. Combined with the preceding report's four attempts, this
is **26 reserved calls, 18 repairs, 24 completed receipts, 131,216 known input +
75,793 known output = 207,009 known tokens, plus two uncertain calls**. Older
campaigns and sessions are additional retained accounting, not reset allowances.

The earlier uncertain sessions were checked again and remain byte-for-byte and
timestamp-for-timestamp unchanged across their session/request/receipt/budget
record sets:

- `367c0d3a…`: 3 records, digest
  `2be15329fb13f3f94b81fa1c3dbfa17d7e6520153478150ec46900f1ae1f4ee2`.
- `37edc1f2…`: 5 records, digest
  `c5401590cc20d504faff4c7085e6fccb252576917e0ab744e5637804a3967cc1`.

Neither new workflow reached FinalReview or approval. `workflow.execute` did
not run. No SmartGuide clone, dependency installation, build, lint, test,
Copilot review, `review_evaluate`, immutable draft or `review_publish` occurred.
The requested checks remain **not executed**, not passed. There is no publication
identifier, URL or exactly-once result. Progress remains **0/3 complete real
read-only reviews and 0/1 publication**.

No review workspace was created. The failure offer was skipped and the remaining
chat was stopped through normal UI controls. The isolated Server and test
Chromium processes were terminated; their listeners closed. Encrypted evidence,
telemetry, configuration, credentials and previous hosts were preserved. There
were no SmartGuide source changes, pushes, metadata changes, merges, closures,
deployments, comments or reviews.

The immediate external blocker is reliable provider completion and a supported
receipt/accounting recovery for the uncertain chat dispatch. Do not treat another
fresh session as recovery of that request. The retrieval correction is tested
offline but has no successful live planning result yet. Even the earlier completed
response still had unresolved contracts, templates and incomplete business
behavior; provider recovery alone is not proof that the full review will succeed.
