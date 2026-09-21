# Designer capability and result-boundary recovery

## Scope and retained evidence

Started from `07cbac1`; the first correction is `e20ce1a`, followed by the
argument-target correction `e29ee32` on `feat/deterministic-planner-v2`.
Only `e20ce1a` received the single live test described below.
There is no planner redesign, public contract
change, storage migration, budget increase or benchmark campaign.

Original Designer session: `aa6971c5bf8140b5a2b752ebef93e1a2`.
Its final trace is `c64b93ff9689c0f918fefbe1904c7388`. Three model calls completed:
one interpretation and two repairs, with 13,576 input and 7,453 output tokens,
and an estimated EUR 0.253673. The final trace records the stop; the model calls
have their own earlier traces and encrypted journal entries.

The 24-card initial shortlist omitted relevant capabilities present in the
129-entry catalog. Unresolved invocations remained unrepaired. The selected
external read capability declared no output schema, yet the proposed parallel
branches exported its result as a typed business value. Both repairs targeted
individual values, first adding templates and then unchecked JSON parsing.
Neither repair established the missing contracts. Classification: capability
retrieval/recovery miss, missing producer contract, and invalid intent repairs.
There was no provider timeout or incomplete receipt in this session.

The exact original prompt is 680 UTF-8 bytes, SHA-256
`8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`.
It references SmartGuide PR #510. The validation below does not execute that PR
or authorize its publication.

## Small correction

Repair advice now includes the operation's logical identifier in its existing
full-catalog retrieval query. The initial shortlist and four-card advisory limit
are unchanged. Invalid proposed arguments still cannot remove alternatives;
permissions and exact capability validation remain deterministic.

An absent result contract is explicitly identified in repair bindings. A known
contract with an invalid field remains distinct. A boundary requiring a checked
conversion gets an atomic target using the existing business block shape:
operations plus result. The smallest enclosing block is issued alone; siblings
are excluded and duplicate binding context is removed. No new intent type or
persisted mapping was added.

The model may retain the real invocation and add a local `calculate` with an
explicit derived result type and checks for malformed data. The existing runtime
validates that output. The capability schema stays unknown. This does not prove
the semantic correctness of arbitrary generated calculations or replace review
evidence and publication checks.

## Read-only replay and deterministic validation

Sanitized laboratory examples failed before production edits. Coverage includes
recovery of an omitted tool despite malformed arguments, atomic block repair,
large unrelated siblings, nested branches/loops/subflows/cleanup, known schemas,
unchanged and invalid repairs, receipt replay, cumulative counters and stale
approval removal. Real runtime execution of the corrected test workflow accepts
object and JSON-text samples; malformed JSON, absent fields, wrong types, nulls
and arrays fail before the downstream call.

An initial whole-parallel target required 12,586 estimated input tokens on the
retained first proposal and was rejected before reservation. Narrowing to the
existing branch block produced these read-only measurements, including the
response schema:

| Retained revision | Completed calls | Prompt bytes | Schema bytes | Estimated input tokens | Selected targets | Deferred targets |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 3 | 1 | 9,758 | 6,917 | 5,815 | 1 block | 15 |
| 5 | 2 | 14,997 | 6,917 | 7,561 | 1 block | 13 |
| 7 | 3 | 14,179 | 6,917 | 7,288 | 1 block | 13 |

The selected block is `/operations/1/branches/0/body`. These are request-size
estimates, not provider usage. They show that the corrected target fits the
existing 12,000-token input allowance; they do not make the original invalid
intent executable. Historical value-only repair requests retain their original
schemas and receipts.

The original session's 16 encrypted session/request/receipt/budget records have
digest `c50e079f1d259d9653eaea51fc281949084eae6c4eedd655bf675e64d854d778`.
Public KeyVault APIs were used for inspection and replay, without provider calls
or changes to those records.

Release validation on the final corrected source (`e29ee32`):

| Suite | Passed |
| --- | ---: |
| Planning, including 21 new recovery cases | 220 |
| Flow runtime | 853 |
| Flow integrations | 76 |
| Agent.Server | 368 |
| AI and HTTP retry | 222 |
| Total | 1,739 |

All suites ran with `-c Release -m:1 --no-restore -warnaserror`; no tests failed or
were skipped. The Server Release build had zero warnings/errors. The osx-arm64
Native AOT planner publish passed with warnings treated as errors; its binary
passed all eight offline cases, each with one mocked model call and zero repairs.
These offline scenarios are not real repository execution evidence. Frontend
sources did not change.

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.AI.Core.Tests -c Release -m:1 --no-restore -warnaserror
dotnet build src/GnOuGo.Agent.Server -c Release -m:1 --no-restore -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 --no-restore -p:PublishAot=true -warnaserror -o <isolated-output>
<isolated-output>/GnOuGo.Flow.Planning.Smoke
```

## Controlled Designer test

The isolated host uses the existing encrypted provider configuration, with
OpenAi / `gpt-5.5-2026-04-24` / Chat Completions, medium reasoning, eight calls,
two repairs, 12,000 input and 8,192 output tokens. A fresh planning index prevents
background resumption of earlier sessions; telemetry and Agent indexes are also
isolated. Credentials and earlier ledgers are unchanged.

The first local launch used development build output in Production mode; its
Blazor scripts did not initialize. The form created no planning session
(`/api/planning` remained empty), and no model dispatch occurred. The launch was
corrected to use published assets. This was test-host setup, not a failed model
attempt or a replacement for an uncertain session.

The published Server was built with:

```sh
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -warnaserror -o <isolated-host>
```

The first publish command used `--no-restore` and encountered missing RID assets
(`NETSDK1047`). Publishing with restore succeeded without warnings. Existing MCP
binaries were staged for this isolated host; integrations were discovered through
their real contracts. No integration execution was substituted with mocks in the
live Designer test.

### Observed result on `e20ce1a`

The original prompt was submitted unchanged through the Blazor Designer
controls in Chromium, with functioning Blazor interactivity. No planning REST
write or handwritten workflow was used.

| Field | Observed value |
| --- | --- |
| Session | `3fbbd97bd758412f9027e38caeeb3e17` |
| Name | `designer-recovery-20260921-e20ce1a` |
| Submission | 2026-09-21 16:39:16 UTC |
| Stopped | 2026-09-21 16:40:49 UTC, revision 4 |
| Model-call trace | `378e02295d1e6eb142b904cd72c6faaf` |
| Final status | Stopped, before a repair reservation |
| Calls / repairs | 1 / 0 |
| Reported input / output tokens | 7,388 / 5,609 |
| Estimated cost | EUR 0.178599; provider-price estimate USD 0.20521 |
| Model-call duration | 59.81 seconds |
| Session active time | 68.01 seconds; approximately 93.4 seconds wall time |
| Receipts | One completed receipt; no pending request |
| FinalReview / scenarios / executable YAML | Not reached / none run / none generated |

The model completed normally. The invalid proposal contains unresolved
invocations and references into undeclared producer contracts. The new conversion
target logic also incorrectly promoted ordinary argument references to a whole
`/operations` edit. That target required approximately **34,410 estimated input
tokens**, including the response schema, and correctly failed the unchanged
12,000-token limit. No repair call or repair attempt was charged.

Failure classification: **deterministic repair-target selection defect**, with an
explicit input-limit stop. The proposal's independent invalid references and
missing contracts remain blocking. There was no provider timeout, uncertain
dispatch, workflow execution or policy bypass.

Retained diagnostic counts:

| Code | Count |
| --- | ---: |
| `HOLE_UNRESOLVED` | 14 |
| `STEP_TYPE_DENIED` | 9 |
| `BINDING_UNAVAILABLE` | 18 |
| `SCHEMA_INVALID` | 4 |
| `STRUCTURED_OUTPUT_INVALID` | 1 |
| `OUTPUT_REFERENCE_INVALID` | 23 |
| `SCHEMA_REFERENCE_INVALID` | 5 |
| `OUTPUT_TYPE_MISMATCH` | 5 |
| `MODEL_INPUT_LIMIT` | 1 |

### Offline follow-up on `e29ee32`

A sanitized test reproduces the over-broad target with an untyped sensor result,
a typed numeric consumer argument and a large unrelated operation. Before the
fix, it requested the entire operation list and exceeded the input allowance.
Now it retains `/operations/1/arguments/0/value`, the absent producer contract and
the authoritative numeric receiving contract, excluding the unrelated operation.
Only exported block or workflow results can trigger a conversion topology target.

Read-only replay of the fresh proposal now selects
`/operations/2/branches/0/body`: one atomic block, 19 deferred targets, 8,852 prompt
bytes plus 6,917 schema bytes, **5,513 estimated input tokens**. The original
session's replay measurements above remain unchanged. These first batches select
resolved untyped producers, so they contain no unresolved-capability alternatives;
the generic omitted-capability test separately verifies full-catalog recovery,
four advisory cards and unchanged strict eligibility.

A separate read-only preview of deferred advice in original revision 3 still
shows imperfect retrieval. `install_dependencies` includes `cmd_run` among its
four cards, and the publication operation includes `review_publish`.
`checkout_pull_request_head` receives `pull_request_read`, `list_issue_types`,
`get_copilot_job_status` and `get_commit`; it still does not receive `git_fetch`.
These previews neither dispatch requests nor change the selected batch. They
demonstrate access to omitted catalog entries, not complete capability recovery
for this real proposal.

No second live submission followed this correction. The smaller request is not
evidence of a successful model repair, valid workflow or completed PR review.
All independent diagnostics remain in the stored failed session.

### Evidence inspection and preservation

The Designer detail and its Pipeline / LLM calls panel were inspected through
Chromium. The model trace displays one completed interpretation, reported usage,
protocol, duration and cost. Its encrypted request and response expand correctly;
the retained logical documents are 32,869 and 42,102 bytes. These document sizes
include diagnostic framing and differ from the actual prompt/schema sizes. The
initial prompt is 18,447 bytes and its response schema is 10,458 bytes.

The new session's eight encrypted session/request/receipt/budget records have
digest `da27aacf507108cce0ff609750dad3752c020dbd26c546418295ac0cc7e7ac66`.
This digest and the original session digest were checked again after replay and
trace inspection. Original reservations, schemas, receipts and usage were not
rewritten. No additional calls or costs were incurred by inspection.

No approval card was accepted. No repository was cloned, no command/test/review
workflow ran, and no GitHub review, reviewed-repository metadata change, source
push, merge or deployment occurred. Only the isolated validation host and browser are shut down;
their persistence directories and encrypted evidence are retained.

## Remaining limitations

- The authorized live planning test **failed**; FinalReview is not demonstrated.
  The follow-up fix has deterministic and Native AOT coverage, but no live-model
  result is claimed for it.
- Initial shortlisting is unchanged. Relevant capabilities can still be omitted,
  and advisory recovery remains probabilistic within the existing repair budget.
- Undeclared external contracts remain unknown. A model must supply a valid local
  checked conversion when required; examples never establish those contracts.
- Some genuine workflow-level topology corrections may still exceed the input
  limit and must stop explicitly. No extra repair attempts or higher limits were
  enabled to force this case through.
- The two available repairs have not been shown sufficient for this proposal's
  remaining independent capability and binding errors. Further live evaluation
  would require a separately authorized attempt; none was made in this delivery.
