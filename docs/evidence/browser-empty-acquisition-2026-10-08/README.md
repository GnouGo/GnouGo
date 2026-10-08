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
after restart. Validation totals and broader checks are recorded after completion.

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
