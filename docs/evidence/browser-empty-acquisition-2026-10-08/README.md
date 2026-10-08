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
and remaining full-observation/formatting interpretation. The new artifact is
waiting for explicit revision/hash-bound acknowledgment. No Amazon execution,
product visit, workbook writing or live cleanup has started; there is no live
execution-oracle result to claim. Existing request limits and all oracles remain
unchanged. No historical invocation was resumed.
