# Real Server validation after focused repair batching

**Stopped at the provider/uncertain-dispatch boundary.** Four real Server chat
attempts tested PR #597; none reached FinalReview. Three small reproduced fixes
were tested, committed and pushed. The last attempt returned HTTP 500 without a
completion receipt, so it was not resent or replaced. The objective remains
**0/3 complete read-only reviews and 0/1 publications**.

This report records real Agent.Server chat attempts, not benchmark results or
completed repository reviews. Source revisions were `fa399b3`, `4ee4b04`,
`f602600` and `f18d890`. Prior campaigns and stopped sessions remain untouched.

## Setup and scope

A warning-free, trimmed macOS ARM64 Release Server was launched on an isolated
loopback port with dedicated planning-index and telemetry paths. Planning
background processing was disabled. Chromium operated the actual Blazor chat and
selected the existing `desktop-planning-e2e` agent through its normal menu.
Its generic `workflow.plan → workflow.execute` bootstrap was unchanged.

The test retains eight calls, six repairs and medium reasoning. Initial limits
are 64,000 input / 32,768 output tokens; typed repairs retain three targets,
12,000 estimated input tokens including schema, and 8,192 output tokens.
The user removed the former voluntary EUR 20 testing gate. Receipts, accounting,
technical limits and authorization remain active; unavailable cost is unknown.

Real MCP preflight established:

- GitHub identity `guillaume-chervet`, through `get_me`.
- Git and Cmd connectivity and dedicated-workspace policies.
- Copilot SDK connectivity (`pong`) without creating an inference session. Its
  configured BYO provider is OpenAi / `gpt-5.5-2026-04-24`; GitHub Copilot login
  status was unauthenticated. Connectivity alone does not prove inference works.
- [SmartGuide PR #597](https://github.com/AxaFrance/SmartGuide/pull/597) remained
  open, non-draft, unmerged and without auto-merge. Head:
  `0b7899b1944aaab5b742e12c6238825155fa2bac`; base:
  `667b097d205f7a376e7c8fc09f1f88e08a4dc5c6`.
- GitHub MCP supplied the complete two-file diff, manifests and all five
  repository workflow files. Deployment workflows were manual or reusable;
  no review-triggered deployment was found. None was invoked.

The unchanged read-only request's SHA-256 is
`6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649`.
Original requests, responses and accounting remain in encrypted KeyVault records.

## First attempt and deterministic reproduction

Session `27ba05a8f77146aeac36f57023a58c7da3dc46191e54655250185e02bf1bbf10`
started at 06:04:05 UTC. Chat correlation:
`5c136050489f4f3c6146020bf7de5149`.

| Call | Input tokens | Output tokens | Wall time | Output ceiling |
| --- | ---: | ---: | ---: | ---: |
| Interpretation | 10,997 | 14,655 | 179.7 s | 32,768 |
| First typed repair | 4,515 | 886 | 13.2 s | 8,192 |

Both completion receipts are durable: 15,512 input + 15,541 output = 31,053
known tokens. Monetary cost is unavailable, not zero. The repair selected one
target, `/operations/0/value`; its prompt was 19,275 bytes and response schema
2,026 bytes. No uncertain dispatch occurred in this attempt.

The initial proposal contained invalid business bindings and unresolved
capabilities. After the repair, a computation body triggered an engine exception:
validation accepted a function body ending in `return`, but contract inference
parsed the original body as an expression. The generic exception handler then
kept advancing; subsequent repair-context construction encountered a missing
graph. This is a **deterministic builder/coordinator defect**, distinct from the
remaining invalid model proposal and previous provider failures.

The chat was stopped through its normal Stop button at 06:10:40 UTC. Its last
checkpoint remains `generating`, revision 2644, with no pending call; no state
was manually rewritten. Background processing stays disabled. There were two
reserved calls and one repair throughout; the repeated host failures dispatched
no additional inference. Six retained records have aggregate evidence digest
`a4a173e63c557aab805824ddfed1b30e00e67ba36223bfacc85334730fc199c1`.

Four sanitized regressions failed before the correction. The fix uses existing
computation normalization for inference and stops unexpected host exceptions
immediately. It adds no schema, API, phase or inference capability. Explicit
candidate diagnostics still use bounded repair; unknown inferred contracts do
not become valid from examples. Regression execution independently returns the
expected numeric result after compilation.

Read-only replay of the retained proposal now builds its two workflows and
reports **92 blocking executable-validation findings**. No model was called,
checkpoint changed, workflow approved or invalid proposal made executable.

The older uncertain `37edc1f2…` evidence remains five records with unchanged digest
`c5401590cc20d504faff4c7085e6fccb252576917e0ab744e5637804a3967cc1`.
Its unknown repair usage remains unknown; its identity was not resent.

## Validation of the targeted correction

- Planning: 193 passed, including the four new regressions.
- Flow integrations: 76 passed; Agent.Server: 345 passed.
- Workflow runtime: 853 passed; AI/HTTP retries: 222 passed.
- Total affected post-fix tests: **1,689**, none failed or skipped.
- Git (48), Cmd (41), Copilot Core (90) and Copilot MCP (110) also passed during
  preflight; those production components were unchanged by the correction.
- Release Server publish, including frontend targets: no warnings/errors.
- macOS ARM64 Native AOT planner publish and smoke: all eight offline cases
  passed. This is offline regression coverage, not a paid evaluation campaign.

Commands used serialized builds (`-m:1`) and `-warnaserror`:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.AI.Core.Tests -c Release -m:1 --no-restore -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -warnaserror
```

## Retry on the committed correction

After pushing `4ee4b04`, the isolated host was restarted from the fresh Release
publish. PR #597's eligibility and SHAs were reread through GitHub MCP. The same
request was submitted through a fresh chat at 07:18:12 UTC, creating session
`c273962cdb4b962ac6552db204e72b656c472f45467ea16a3ba2a12c34f3f4a4`.
This retry follows a reproduced, tested correction; it does not reconcile or
replace any prior receipt or uncertain reservation.

This attempt stopped at 07:22:20 UTC, revision 10, after **eight calls and five
repairs**: one interpretation, two bounded capability choices, then five typed
repairs. All eight receipts completed. Usage was **34,358 input + 17,314 output =
51,672 tokens**; monetary cost remains unknown. Chat correlation was
`0a04662aac269969f12a7d0fcb6fd0db`. The normal designer-revision offer was skipped.

The original computation exception did not recur. The remaining failures were
invalid model-authored intent and a **capability retrieval miss during repair**.
The full allowed catalog contained `pull_request_read`, but none of the four
advisory cards included it. A correction used that tool's name instead of its
issued ID. Subsequent repair advice still omitted the exact allowed name match;
the model then replaced the real metadata read with incomplete local context.
That proposal never reached FinalReview. Its invalid bindings, incomplete
contracts and call exhaustion remained blocking; no placeholder evidence was
accepted as real execution.

A sanitized six-capability reproduction confirms that verbose distractors can
hide an allowed tool whose exact name is present in the rejected invocation.
The focused correction (`f602600`) promotes only exact allowed tool-name matches in repair
advice. It neither accepts aliases nor assigns capabilities automatically, and
it retains four advisory cards and complete deterministic choice validation.
The regression failed before the change and passed afterward, including strict
rejection of the original name and a valid issued-ID correction reaching review.

Its affected suites passed 1,690 tests, and the warning-free Release Server
publish and eight-case Native AOT smoke passed again. The 18 encrypted records
for `c273962c…` have digest
`50d5935b064697d9f1bd8da753ac61ca49deb7fcbbf9996d55122bc1ae4e3558`.

After pushing this correction and rechecking the same PR's unchanged eligibility
and SHAs, the unchanged prompt was submitted again through Server chat at
07:29:24 UTC. Session:
`85ddf6586b36d8de7b47f3a738b630a5ec938fa6dc19e5a3b50a42f84ff0927e`.
Chat correlation: `6439cde8afa724ff0421e25f37242eef`. It stopped at 07:39:05 UTC,
revision 9, after one interpretation and six repairs. All seven receipts completed:
**41,152 input + 22,958 output = 64,110 tokens**. Monetary cost remains unknown.
The designer-revision offer was skipped; nothing was approved or executed.

This run exposed a second **deterministic type-inference defect**: arrays of
different authored string literals were checked against the first item's
singleton enum. A valid array therefore became a schema hole, including inside
an otherwise typed object. Repairs to the enclosing declaration could not fix
that inference error. Independent unresolved capabilities, invalid templates and
bindings also remained blocking. The name-hint retrieval correction was not
sufficient to make this proposal executable.

Commit `f18d890` derives the string-item enum from all distinct authored elements.
Two sanitized flat/nested array reproductions failed before this three-line
correction and passed after it. A third regression proves that a narrower
authoritative capability enum still rejects the result. Independent compiled
execution returns both expected elements. There is no sample-based inference,
automatic value replacement or change to permission/approval contracts.

All **1,693 affected tests** passed, followed by warning-free Release Server
publish and the eight-case Native AOT smoke. The isolated host was restarted
from that committed correction before another request.

## Final retry and provider stop

After another real GitHub MCP eligibility/head check, the same prompt was
submitted at 07:41:53.997 UTC on `f18d890`:

- Session: `367c0d3a0adc34e3ca5af48b669601b14d978a72a057ce74a85356d884b2a287`.
- Chat correlation: `fa018f32e619def5ac5547206eb6ca29`.
- Reserved interpretation:
  `367c0d3a0adc34e3ca5af48b669601b14d978a72a057ce74a85356d884b2a287:1:fa167f6e3050e971017e0c785bad1d269f50d27656c6b4dae26d7fbc91eac7be`.
- Reservation: 07:41:57.936 UTC; stopped checkpoint: 07:46:59.254 UTC,
  revision 2, one call and zero repairs.
- Transport evidence: HTTP **500**, `AttemptCount=1`, `RetryExhausted=True`.
  Dispatch-to-stop wall time was approximately **301.3 seconds**. The transport
  log's `ElapsedMs=0` does not represent that observed wall time.
- Diagnostic: `MODEL_DISPATCH_UNVERIFIABLE`. There is **no completion receipt**.
  Token usage and monetary cost for this dispatch remain **unknown**, despite
  the legacy aggregate snapshot containing zero token/cost values.

This is a **provider/transport failure**, not proof of a local timeout or another
planner defect. The original pending request/schema/reservation remains intact.
The designer-revision offer was skipped through the UI. No new identity,
automatic resend or replacement session followed this uncertain dispatch.

The final session's three retained records have digest
`2be15329fb13f3f94b81fa1c3dbfa17d7e6520153478150ec46900f1ae1f4ee2`.
The preceding `85ddf658…` session has 16 records with digest
`bb377b6b7638f711164b5aac199d68ea1fd0b1dfe585ac3e67104eda99a431a2`.
The earlier two attempts and old `37edc1f2…` evidence hashes were rechecked and
remained unchanged. Digests include record keys and update timestamps.

## Accounting, execution and cleanup

| Revision | Session prefix | Reserved calls / repairs | Completed receipts | Known input / output tokens | Outcome |
| --- | --- | ---: | ---: | ---: | --- |
| `fa399b3` | `27ba05a8…` | 2 / 1 | 2 | 15,512 / 15,541 | Host inference exception; chat cancelled |
| `4ee4b04` | `c273962c…` | 8 / 5 | 8 | 34,358 / 17,314 | Invalid intent, retrieval miss; call limit |
| `f602600` | `85ddf658…` | 7 / 6 | 7 | 41,152 / 22,958 | Literal-array inference defect; repair limit |
| `f18d890` | `367c0d3a…` | 1 / 0 | 0 | Unknown / unknown | HTTP 500; uncertain dispatch |

Across these four attempts: **18 reserved calls, 12 repairs, 17 completed
receipts, 91,022 known input + 55,813 known output = 146,835 known tokens**, plus
one uncertain dispatch. Monetary cost is unknown. These totals exclude all
previous campaigns and sessions; their charges and uncertain usage were not
reset. Copilot inference was never dispatched, so there is no Copilot review
usage or execution evidence to report.

Actual operations were Server chat planning, MCP discovery, read-only GitHub
preflight, Git/Cmd policy reads and a Copilot SDK connectivity check. **No
generated workflow reached approval**, and `workflow.execute` never started.
No real clone, dependency installation, build, lint, unit/integration check,
Copilot review, `review_evaluate` or `review_publish` ran for SmartGuide. The
requested repository checks are **not executed**, not passed. No immutable review
draft, publication ID, review URL or exactly-once publication result exists.

No review workspace was created, so repository-workspace cleanup was not needed.
The isolated test Server and Chromium were stopped after the final failed chat
ended. Encrypted sessions, receipts, index and telemetry remain retained; no
existing host or unrelated workspace was removed. There were no SmartGuide
source edits, source pushes, metadata changes, merges, closures, deployments,
comments or reviews. Only GnOuGo fixes and this redacted report were committed.

Final published-binary verification also passed:

```sh
GnOuGo.Agent.Server --planning-persistence-smoke <temporary-isolated-directory>
```

It verified schema-7 restart, encrypted storage, optimistic revisions, tenant
isolation and uncertain-publication replay using separate temporary databases
and no external effects. Combined with the unchanged integration component
suites, **1,982 tests passed** during this work; the final affected-source rerun
contains the 1,693 tests listed above. No frontend sources changed; Release
publishes built the configured frontend targets. No paid synthetic campaign ran.

## Remaining limitations and next gate

The immediate blocker is reconciling the final uncertain provider dispatch
through a supported receipt/accounting path and restoring reliable provider
completion. A new request must not be used to bypass that reservation. Provider
billing controls were not investigated, as instructed.

The three fixes have deterministic reproductions and release validation; the
last combined revision **does not have successful live planning evidence**.
Prior model proposals still had unresolved capabilities, invalid templates and
bindings, missing contracts and incomplete business behavior. Safely rejected
proposals are not policy violations, but they are also not successful reviews.
No architecture redesign, public API/storage change, prompt-specific branch,
budget increase, weakened validation or approval bypass was introduced. PRs #595
and #553 were not promoted to execution because the first review never passed.
