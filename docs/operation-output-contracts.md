# Operation output contracts across scopes

Designer session `2644c09cd24446e2b18cb9f355922dd8`, trace
`2e50b73d8a8163dae50fbea943aa26a3`, stopped before execution with
`TASK_COMPILER_VALIDATION: SCHEMA_INVALID` at
`/tasks/review_when_clone_available/body/outputs/checksReport`.
The exact failure was reproduced from encrypted records through public KeyVault APIs.
The original nine revisions and four model responses remain unchanged.

## Cause and correction

The output exports an entire `agent.run` result. Semantic compilation used its generic
envelope, including `output: {}`, while graph validation already knew about the approved
literal `output_schema`. Propagating that declaration alone exposed a second problem:
open objects in the payload and unspecified contents in the envelope were rejected at
workflow boundaries.

One internal resolver now derives effective operation results for semantic validation,
lowering and graph validation. Only `agent.run` specializes its payload from its approved
declaration, because that executor validates the declaration at runtime. MCP outputs
continue to come from their exact authoritative producer contracts.

The existing opaque representation preserves unknown contents inside known object and
array shapes. Import traverses schema positions, never instance values inside
`const`, `enum` or defaults. It preserves constraints, requiredness, nullability and
closed objects. It does not invent field types or permit typed consumers to narrow
opaque values. Discovery contracts, versions and issued requests remain unchanged.

A related deterministic availability omission prevented checked field exports from
successful agent tasks. Agent result envelopes now participate in the existing guarded
export proof. Skipped producers and error continuations still do not establish
availability. Compiler failures remain outside model repair; diagnostics now identify
nested schema pointers and the originating business producer when available.

TaskPlan, PlanningGraph, planning storage, runtime and MCP contracts are unchanged.
There is no new representation, model phase or scenario-specific production rule.

## Retained evidence and execution coverage

The [sanitized recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/OperationOutputs/retained-output-contracts.json)
retains all four responses, issued schemas, accounting and failure history. Local user
paths and repository URLs are replaced consistently. Only the fixture's repair
authority is rebound to its sanitized contracts; the original encrypted request is
untouched, and the original authority remains recorded in the fixture's sanitization
note.

| Retained session replay | Before correction | After correction |
| --- | --- | --- |
| Planning result | Stopped: compiler schema failure | Final review, artifact validation passed |
| Recorded planning calls | 4 | 4 |
| Recorded repairs | 1 | 1 |
| Additional provider calls | 0 | 0 |
| Original business workflow executed | No | No |

[Replay regression](../tests/GnOuGo.Agent.Server.Tests/RecordedOperationOutputTests.cs)
checks unchanged plans, discovery receipts, issued requests, counters and usage through
restart recovery, plus invalidation of modified approval artifacts and repair baselines.

[26 focused regressions](../tests/GnOuGo.Flow.Planning.Tests/OperationResultContractTests.cs)
include 17 execution variants through the real Flow engine with deterministic injected
agent runners and in-memory MCP handlers. They cover whole and named results, nested
unknown data, root/sequence/capture/conditional/parallel/group/loop exports, both
conditional branches, and exact exported payloads. Three variants require execution
failure for denied scope, missing verification evidence or an invalid payload. Other
tests reject invented fields, incompatible consumers, malformed declarations and
skipped/continued producer availability; they also check constraint preservation and
diagnostic provenance. Unsupported producer imports remain compiler failures rather
than repairable task-input errors.

These are deterministic integration results. They do not establish real Copilot
sandbox execution or remote review publication. No paid evaluation or external
repository execution was performed. The historical stabilization campaign's **33/33**
belongs to candidate `7d5d42b2`; its corpus and measurements are unchanged and did not
cover this defect.

## Validation

The final solution run passed **3,792 tests across 33 projects**, with 12 existing
skips and no warnings under `-warnaserror`. This includes **688 planner tests** and
the retained-session regression. Release packing and the published **osx-arm64 Native
AOT** smoke passed. [Validation metadata](evidence/operation-output-contracts.json)
records changed source/test hashes and retained local log hashes.

## Reproduction

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~RecordedOperationOutputTests
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -o artifacts/operation-output-contracts/aot
artifacts/operation-output-contracts/aot/GnOuGo.Flow.Planning.Smoke
```

The Native AOT smoke includes approved payload specialization, nested opaque contracts,
source-generated serialization and YAML compilation with host confirmation guards.
