# Empty Browser acquisition recovery

The retained live returned a successful zero-record initial snapshot with an empty
title and no HTTP status. Its workflow skipped search/visits and wrote a status
row; the independent execution oracle failed. [Original evidence](../conditional-entry-2026-10-08/README.md)
and [sanitized acquisition](retained-empty-entry.json) remain unchanged. Those
receipts do not establish a CAPTCHA or the cause of the empty document.

Complete acquisition now detects a wholly unusable document conservatively and
permits one reload only for its explicit, verified successful GET navigation. The
reload shares the original deadline and three-attempt ceiling. It discards the
whole old generation; current-page reads, interactions, submissions, HTTP failures
and unknown methods cannot trigger it. Genuine empty selected regions and non-text
content remain valid. Persistent or ineligible emptiness returns `OBSERVATION_EMPTY`.

Current-document navigation metadata is distinct from the latest HTTP response;
a response that did not commit a document is not presented as that document's
status. Optional bounded recovery details survive structured MCP errors, encrypted
receipts and restart. No planner, mapping, runtime budget or execution oracle changes
are included. Formatting inference and duplicate analysis remain deferred.

## Validation

Browser regressions cover recovery, no recovery, exact-generation records, expired
references, redirects, HTTP/no-content responses, submitted/current-document reads,
shared deadlines, cancellation, failed reload and historical metadata serialization.
The transport regression requires a durable failed receipt, zero downstream model
calls or workbook writes, completed cleanup, tenant isolation and no re-navigation
after restart. [Validation](validation.json) records 136 passing Browser cases,
38 local execution/receipt cases (including independently checked XLSX values),
and five encrypted-receipt cases using the Release self-contained `osx-arm64`
Browser executable. Browser Native AOT remains unsupported.

The broader solution run encountered an unrelated synchronous telemetry-queue
wait in a chat-planning test. [Retained stacks](solution-interruption.json)
identify the boundary; no Browser acquisition was active. The hung deterministic
test process was stopped, and remaining host classes ran unchanged in a fresh
process. [Coverage accounting](host-test-coverage.json) reconciles all 709 host
cases by stable test ID: 708 passed and one existing skip, with no missing cases.
This does not establish that the uninterrupted-suite hang is fixed. No telemetry
production code, assertions, timeouts or test limits were changed.
Across all 33 projects, including the isolated remaining host cases, the recorded
coverage is **4,939 passed, 13 existing skips, zero failed assertions**. The original
solution command exited 1 after the interrupted host process and is not reported
as green. Frontend production builds completed. At evidence publication,
[frozen-candidate CI](ci-status.json) had 21 successful, three skipped and eight
still-running checks; pending jobs are not counted as passed.

```sh
dotnet test tests/GnOuGo.Browser.Mcp.Tests -m:1 -warnaserror
env Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true --filter 'FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~LocalProductOutcomeExecutionTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

## Fresh live boundary

Only one new Amazon execution is authorized, maximum ten products, after focused
local validation and configured pricing/currency readiness. Retain concrete
revision/hash-bound review and explicit requirement acknowledgments. The campaign
ceiling remains EUR 150; pre-dispatch upper bound is EUR 112.681591, including all
unknown reservations. No historical invocation may be replayed. Planning, execution
and independent oracle outcomes will be reported separately; PR #117 remains draft.

## Fresh planning result

Production and harness are frozen at `e04173619b3a3444586cc185fb3e7f64cb4dd820`;
[binary and oracle hashes](frozen-binaries.json), configured
[provider readiness](provider-readiness.json) and the disposable local
[execution readiness probe](execution-readiness.json) are retained separately.

The new run `emptyacquire20261008a-amazon-1` reached final review at revision 4,
artifact `c243211195ab1f815c0464005fbc45c4c11abd922a54f9b7330466059ef4f028`.
Its initial proposal was rejected for nine nonboolean `requires` values. One
[explicit scoped revision](revision-command-r2.json) corrected only those slots;
operations, objectives, interfaces, bindings and cleanup stayed identical.
The rejected proposal and diagnostics remain in `task-plan-r2.json` and
`review-r2.json`; the new artifact has no validation diagnostics.

Planning used three logical calls, four physical attempts, zero automatic repairs,
two discovery reads and 401.478 seconds. Verified usage was 29,245 input / 6,540
output tokens, costing EUR 0.3038645843. Campaign upper bound is now
**EUR 112.9854556947 / 150**, including unchanged EUR 5.2033274578 unknown
reservations; no new uncertain call was created.

[Concrete artifact review](amazon-review.md) exposes the six accepted requirements
and remaining full-observation/formatting interpretation. The user subsequently
[approved revision 4 and all six requirements](execution-approval.json). The exact
artifact executed once; its result follows. The pre-execution review and generated
YAML remain unchanged. Existing request limits and all oracles remain unchanged.
No historical invocation was resumed.

## Approved execution: acquisition succeeded, E2E failed

[Readiness was rechecked](execution-readiness-approved.json), all seven frozen
binary/source/oracle hashes matched, and the existing revision/hash-bound
[approval command](approval-command-r4.json) was accepted. Approval advanced the
session to revision 5 without regenerating the artifact.

The initial explicit GET returned HTTP 200 with a nonempty title and **133 records
across three complete pages**. It required one capture and zero recovery reloads.
The subsequent current-page read returned **263 records across six complete
pages**, also in one capture, with a distinct snapshot ID. Its HTTP method/status
remain unknown because this read performed no verified navigation; the previous
GET status was not borrowed. No truncated observation was accepted. The empty
recovery path itself was not exercised live; its evidence remains deterministic.

The first interpretation completed. The second (`selectFreshSearchControls`)
sent the complete fresh observation directly to global interpretation and failed
admission with **`LLM_BUDGET_EXCEEDED`, `dispatch_status: not_started`**.
[Read-only replay of the frozen admission helpers](request-size-inspection.json)
reproduces the rejection with zero inference:

| Request | Prompt characters | Prompt-only estimate | Complete request estimate | Serialized bytes |
| --- | ---: | ---: | ---: | ---: |
| Initial controls | 60,514 | 32,535 | 51,006 | 85,949 |
| Fresh controls | 136,420 | 72,213 | **108,576** | 185,904 |

The unchanged input allowance is **96,000**. Estimates include the existing
conservative framing; the admission check sizes the full serialized request,
including schema and JSON escaping, not just the prompt. This is a per-request
input limit, not exhaustion of the EUR 150 campaign.

The encrypted journal records verified completion for that pre-dispatch failure,
25 normal steps, one finalization step and completed Browser cleanup. The oracle
independently confirmed no active Browser page remained. **Zero search submissions,
zero product visits, zero dynamic mappings and no XLSX** occurred. The unchanged
oracle failed with `workflow_execution_failed` and `workbook_missing`; no successful
E2E is claimed. The failed invocation must not be replayed.

[Execution evidence](execution-result.json): one provider call, 22,855 verified
input / 304 output tokens, EUR **0.109499512**, 23.494 seconds including oracle
verification. Total planning plus execution: EUR **0.413364096**, 424.972 seconds.
Campaign upper bound: **EUR 113.094955207 / 150**, including unchanged EUR
5.203327458 unknown reservations. No new uncertain provider completion occurred.

The remaining composition issue is passing full observations to a global consumer
instead of a declared compact decision view while retaining authoritative action
data separately. This execution did not change planner, mapping or limits to mask
that issue. No second live was dispatched, and PR #117 remains draft.

Reproduction commands (inspection is read-only; **do not repeat execution**):

```sh
# Run from the frozen e0417361 checkout with its retained binaries.
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability provider-readiness --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002 --run emptyacquire20261008a-amazon-1
```

The retained encrypted record contains full observations and receipts. Published
evidence contains counts, acquisition metadata and errors, not page contents.
