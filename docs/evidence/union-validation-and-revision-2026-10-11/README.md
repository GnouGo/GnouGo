# Union validation and disposable composition revision

This is offline evidence for PR #117. No paid planning or historical workflow execution occurred. The retained run `sequential20261011a-amazon-1` remains stopped at revision 2 on its original `1cc0fe8a` build; its two requests, receipts, manifest and financial reservations are unchanged.

## Static correction

Production changes are confined to expression/schema validation. The validator retains the original target schema, checks coherent alternatives against the entire supplied value, and keeps sibling properties, items, requiredness, `allOf` and local references. It does not take the first object descriptor or combine fields from different alternatives. Known literal tags and authoritative string domains constrain compatibility; unknown values cannot prove constants or unique `oneOf` membership. Fully resolved runtime validation still happens before MCP dispatch.

The simple-contract path retains its existing diagnostics. When all alternatives fail, expression errors identify conflicting fields and aggregate their expected types deterministically. No Document schema reordering, runtime schema change, compiler change, new profile, permission, budget or repair authority was introduced.

[Before](offline-before.json), the exact retained proposal fails at the writer's typed `rows` reference: array incorrectly compared with the first branch's null constraint. Literal rows and a reversed clone of the same schema passed. The corrected validator accepts typed rows under either order and still rejects incompatible common item constraints, mutually exclusive arguments, cross-branch field combinations, invalid nulls and exact `oneOf` overlap.

The original TaskPlan generates **identical v9 YAML before/after**: 75,217 UTF-8 bytes, SHA-256 `86de0c717fb8ed177d5bab3ec16b1fae77ea4bb16889e49d99f469482ae3730b`. Only its static acceptance changes. `expression_contracts`, runtime checks and historical lowering remain intact.

## Explicit composition correction

The actual issued requests already included bounded `each`, unchanged IDs, verbatim block selection and no-unrequested-work guidance. This was not stale configuration. The model's response did not follow it; static validity alone does not establish business acceptance.

The existing structural `revise` mechanism is exercised on a disposable copy, preserving accepted requirements and public output contracts. It removes the unrequested action branch and replaces adaptation only:

- Complete bounded pages → independent candidate arrays → explicit one-level flatten.
- Compact product IDs/text → selected IDs → offered lookup → original-record lookup → individual navigation.
- Complete product pages → observed candidate facts. A typed projection gives description judgment only ID and description, retaining other facts separately.
- Selected description IDs are checked against offered candidates, then resolved against original record text. A declared missing description remains null; it is never replaced with a heading or interface control.
- Non-null product records retain nullable fields. Typed row assembly writes actual structured XLSX rows. Status is a typed predicate over writer/cleanup results, with no formatting or status inference.

Necessary technical scopes retain original record arrays, validate/reconnect identities, preserve explicit null descriptions and assemble bounded rows. These checks add work compared with the incorrect retained composition; this correction does not claim a compiler-size reduction or change v9 lowering.

The local fixture uses real Browser and Document MCP discovery/transport, a local HTTP site and deterministic inference adapters. Its independent oracle checks ten ordered visits, original action URLs, verbatim cells, blank positions, the writer's actual path and Browser closure. Completed encrypted-run recovery repeats neither navigation nor inference. An observed “Report a problem” control is not recorded as a business error. Search candidates outside mapping examples still reach the decision.

## Validation and measurements

- Retained encrypted replay: zero provider/MCP calls, clean compilation and disposable revision to `final_review`. The cloned session retains cumulative calls **2 → 3**, zero repairs and unchanged requirements; no artifact is approved. [Exact replay findings](offline-after.json), [corrected proposal](corrected-proposal.json).
- **1,270 Flow tests pass**, including typed/literal/assembled rows, alternative order, common constraints, local references, deep tagged unions, known enums, nullable values and runtime rejection of unresolved `oneOf` overlap before dispatch.
- **1,253 Planning tests pass** in the full solution. Both final local Browser/Document revision cases pass: normal descriptions and explicit missing description. Existing negative/recovery cases run in the host suite. [Independent local measurements](local-oracles.txt).
- Core/Planning/Persistence Release packages, Planning Native AOT smoke, Document Native AOT transport tests and published encrypted server recovery pass. [Published checks](published-checks.json). Skill validation passes. The disposable release build's generated metadata timestamp was restored; model metadata values and limits were unchanged.
- **The full solution gate is failed:** 5,213 passed, 13 skipped, one unrelated Copilot cancellation-fixture teardown failure (`Directory not empty: <temporary-fixture>/leases`). The isolated rerun passes, but this does not establish a fix or clear the gate. No assertion, timeout or production Copilot behavior was changed. The final Flow run additionally includes one new dispatch regression; the final consumer-view fixtures were revalidated separately. [Complete suite inventory and failure](validation.json).

Composition comparison, separately from the unchanged compiler output: **14 → 19 tasks, 30 → 67 steps, 9 → 40 sets, 74,949 → 200,332 YAML characters**. The corrected local executable is 200,410 UTF-8 bytes. Adding the narrow description projection does not add compiled steps or invocations under existing v9 fusion. These are increased validation/reconnection costs, not a claimed size improvement. [Measurements](measurements.json).

Each final local case processes **29 pages / 12 offered candidates**, visits **10 products**, checks exact XLSX cells/order/blank positions, and verifies cleanup plus encrypted recovery. It uses 331 actual invocations (330 normal steps, one cleanup step), 11 deterministic mapping-adapter calls and 12 genuine decision/conversion-adapter calls; **zero inference for typed row/status assembly**. The complete serialized product-decision request is **2,597 bytes**. All fixture inference requests remain under the existing 12,000-token fixture allowance. Journal logical sizes are reported in the local output, not confused with physical encrypted storage.

## Reproduction

Build the owning tests and run the retained replay only in a disposable console project. [replay.cs](replay.cs) uses public KeyVault APIs and the existing deterministic test adapter; it never constructs a provider client or advances the persisted historical session. Its only writes are to the explicitly supplied evidence directory. Reference the current `GnOuGo.Agent.Server.Tests/bin/Debug/net10.0` dependencies, copy that output's `Fixtures` directory and native SQLite dependency, and execute with the authorized workspace and a disposable output directory. The original reproduction is retained in [the earlier evidence](../sequential-live-2026-10-11/replay-rows.cs).

```sh
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true \
  --filter 'FullyQualifiedName~BusinessCompositionExecutesRealVisitsAndWorkbookWithLocalAssertions&DisplayName~union_' \
  --logger 'console;verbosity=detailed'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

Published validation used an isolated checkout of the same production changes on `osx-arm64`. The commands below reproduce the owning checks; use separate disposable output directories:

```sh
python3 scripts/ensure-otlp-protos.py
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror -o /tmp/union-packages
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror -o /tmp/union-packages
dotnet pack src/GnOuGo.Flow.Persistence -c Release -m:1 -warnaserror -o /tmp/union-packages
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -o /tmp/union-planning
/tmp/union-planning/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Document.Mcp -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false -p:SkipModelMetadataGeneration=true -o /tmp/union-document
GNOUGO_DOCUMENT_MCP_TEST_EXECUTABLE=/tmp/union-document/GnOuGo.Document.Mcp GNOU_GO_DOCUMENT_MCP_TEST_EXECUTABLE=/tmp/union-document/GnOuGo.Document.Mcp dotnet test tests/GnOuGo.Document.Mcp.Tests --no-build --filter 'FullyQualifiedName~StructuredRowsTests|FullyQualifiedName~Contract'
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -p:SkipClientBuild=true -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true -p:SkipModelMetadataGeneration=true -p:UseAppHost=true -o /tmp/union-server
/tmp/union-server/GnOuGo.Agent.Server --planning-persistence-smoke /tmp/union-disposable-recovery
```

The failed proposal and unchanged requirements are retained under `tests/GnOuGo.Agent.Server.Tests/Fixtures/UnionRevision`; the existing test builder produces the revised counterpart. No response schema, original issued prompt or historical approval is rewritten.

## CI and source identity

[Source hashes](source-hashes.json) identify the tested production, compiler, skill and benchmark files. [CI snapshot](ci-observed.json) is explicitly time-bound: several desktop/host/planning jobs were still running when recorded. Stable unit checks and the completed publish jobs passed; this is not a claim that all CI is green. The local full-suite failure independently keeps the paid gate closed.

## Live boundary

Candidate `73afa820` and fresh identity `unionrevision20261011a-amazon-1` are staged in a clean disposable checkout. The live manifest, pricing/currency recheck and paid session are **not started**, because the full-suite gate is still failed. [Preparation status](prepared-candidate.json). **New authorization is required before any paid planning**, followed by the new artifact's revision/hash-bound approval and explicit requirement acknowledgments before execution. Maximum ten products, existing independent `observed-products-v2` oracle and €150 ceiling remain unchanged. No historical run can be resumed on the corrected build. PR #117 remains draft; local success is not live acceptance.
