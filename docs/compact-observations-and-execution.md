# Compact observations and reliable workflow execution

The architecture remains LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML. No additional planning phase, executor or intermediate representation is introduced.

Fresh requirements declare the accepted public output names and types alongside inputs. The compiler checks that the final output interface implements them; changes require explicit revision and fresh approval. Optional absent declarations remain omitted in historical serialization. This is interface validation, not proof that arbitrary prose was implemented. Independent execution oracles remain authoritative.

Browser `format: observation` returns bounded visible records with DOM grouping, selectors and observed URLs, preserving the existing content field. The response caps, explicit truncation and snapshot continuation are documented in [Browser MCP](../src/GnOuGo.Browser.Mcp/README.md). No model summarizes or interprets the page during capture. Planning prefers narrow observations and several independently verifiable agent tasks with cumulative budgets. Missing command capabilities require an existing compatible approved operation or an explicit limitation.

Optional `gnougo.artifacts.locations` extends the existing version-1 artifact metadata:

```json
{
  "version": 1,
  "locations": [{
    "pointer": "/filePath", "outputPointer": "/filePath",
    "kind": "file", "action": "materialize", "space": "file://producer/workspace/"
  }]
}
```

Input pointers must resolve required string contracts. `kind` is `file`, `directory` or `handle`; `action` is `use`, `materialize` or `release`. File/directory spaces are absolute base URIs; handle spaces are opaque namespaces. An optional output pointer relates a materialized resource to its declared output. Optional `selectorPointer` and `selectorValue` select one existing literal input-schema discriminator branch. Invalid or ambiguous declarations are rejected during discovery. Document writing publishes its file relationship; configured Cmd parameters publish only explicitly declared relationships. Names and scripts never imply one.

Existing graph validation checks known addresses for use after release and returned artifacts inside released directories, following identity-preserving exports and exclusive branches. Unknown/dynamic addresses remain an explicit review limitation. Metadata neither grants deletion permission nor establishes filesystem existence, ownership, symlink identity or completion of business work. Tools without this metadata remain usable. Requested reports must actually be materialized outside released temporary resources and verified independently.

Executor cleanup retains public APIs. `workflow.execute` already delegates exact approved-artifact execution to `WorkflowCallExecutor`; `workflow.plan` remains the leased planner/interaction adapter. `workflow.route` shares the existing scoped child execution, journal, mapping cache and cumulative limits, removing configuration copies and duplicate dispatch loops. It no longer substitutes the first candidate for an invalid model selection. `mcp.list` retains its cache and output contract; its obsolete mandatory planning instruction is removed. `sequence` retains its runtime scope.

Learned mapping profile 4 rejects comparisons/truthiness of opaque observed tokens. Use `m.test(token, pattern)` for text predicates and `m.has` for presence, then return observed tokens. The existing one-generation/one-repair limit and source-grounding checks are unchanged. Cache identity includes the profile version. A local workbook oracle caught an otherwise well-typed empty extraction caused by a token/string comparison; the oracle remains unchanged.

## Reproduction

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
```

The product fixture uses real Flow, local HTTP, Chromium and Document MCP processes, deterministic inference, and independently inspected XLSX cells. Provider-backed evidence is recorded separately. Historical benchmarks, saved workflows, approvals and uncertain external invocations are unchanged.

## Validation evidence

[Deterministic measurements](evidence/compact-observations/validation.json): 4,223 solution tests passed with `-warnaserror`, zero failures, 12 explicit platform/paid-test skips. Nine product cases passed against published Browser and Native AOT Document processes; each used one planning call, two discovery reads and zero repairs. Runtime inference was a deterministic adapter, not a provider call. The independent workbook oracle retained expected navigation failures, denied writes and collection-limit rejection.

The local page produced 15,109 serialized observation characters across all pages versus 99,060 HTML characters (84.75% reduction, 74 records). This measures representation size, not verified provider token savings. Generation-rule source text increased from 2,166 to 2,220 characters. Executor implementation shrank by 126 net lines; no new executor was introduced.

Release packages, planning Native AOT, Cmd/Document Native AOT publication, Browser publication, trimmed Server encrypted recovery, the production frontend build and Designer browser checks passed on macOS arm64. Windows-only command tests and paid Copilot unit-suite cases remain separately skipped. Published command tests passed 93 cases. The first AOT smoke exposed an outdated synthetic requirements response; its output declaration was updated and the complete smoke passed.

Live evaluation uses the existing frozen harness/oracles and `schema-portability-20261002` campaign. Read its ledger before dispatch. Use fresh run IDs and keep prior uncertain invocations stopped:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect --campaign schema-portability-20261002 --workspace /path/to/workspace
# On the clean frozen candidate, run readiness for both cases, then planning.
# Review the retained artifact before execute with its exact --artifact-hash.
# Use --schema-portability mapping --run <fresh-id> for the untyped matrix.
# Use --schema-portability report --cohort <cohort> for comparable execution evidence.
```

Neither compilation nor deterministic local execution establishes success on Amazon.fr or the pinned external repository. Live results will be retained separately; the PR remains draft until its execution gates pass.

## Live result: incomplete

[Retained live evidence](evidence/compact-observations/live.json), candidate `a16f12a3f8e838c72c9d235fd98f33e2b886caa0`: the first untyped-MCP planning request stopped after 1,215,369 ms, one logical planning call, four physical transport attempts, one discovery read and zero repairs. Three attempts returned HTTP 500 with gateway code `ESG120` and message `Routing failed`; the fourth has no verified completion. The session correctly stopped with `MODEL_DISPATCH_UNVERIFIABLE`. No request was replayed or reconciled.

There are no verified usage receipts for this request. The ledger retains €35.45388838 of verified historical cost plus €1.30337639 reserved for unknown completion, for a €36.75726477 upper bound under the €50 ceiling (about €13.24 remaining). Unknown tokens/cost are not reported as zero. Paid dispatch stopped after the repeated external gateway failure.

**Acceptance remains unmet:** 0/9 untyped execution cases completed; the three Amazon and three code evaluations were not started. No execution success is claimed. Local readiness passed Browser cleanup, independent XLSX reading, code MCP discovery and toolchain availability, but does not establish external execution. Restore the configured provider gateway route before collecting a fresh cohort, preserving the unresolved request and its reservation. Keep PR #117 draft. Historical 33/33, mapping and failed live cohorts remain unchanged.

[CI status snapshot](evidence/compact-observations/ci.json) distinguishes completed checks from still-running jobs.
