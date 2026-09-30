# Business-intent refactor validation — 2026-09-19

> Historical pre-schema-9 evidence. Current architecture and migration: [schema 9](workflow-planning-v9.md).

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

The planner now accepts compact business operations and constructs executable workflows deterministically. The eight offline cases pass generation and independent execution. Initial request size on the large-catalog case fell 73.0%. **Live-model reliability remains unmeasured for the candidate**: the baseline campaign stopped at an uncertain second dispatch and was not replaced or retried.

## Delivered architecture

`WorkflowIntentPlan` no longer mirrors `PlanningGraph`. It has inputs, operations, outputs, optional named subflows and questions. Discriminated operations express invocation, calculation, model transformation, conditions, iteration, parallel groups, subflow calls and cleanup. Known schemas, result channels, MCP envelopes, fixed arguments, runtime paths, technical defaults, helper functions, retries and confirmation configuration are absent from intent.

The builder owns contract inference, dependency ordering, scopes/captures, native control flow, transport envelopes, fallbacks and cleanup guards. The compiler preserves authoritative constraints and defaults in native port schemas. Static inference never uses samples to establish executable types. All singleton choices remain model-free. Compact cards shortlist at most 24 capabilities; unresolved operations search the full allowed domain with operation-specific context. Local corrections replace issued business targets atomically, followed by complete validation. Literal fixtures are requested only when deterministic samples cannot satisfy contracts.

Removed graph-mirroring DTO fields, initial fixture payloads, model-authored executor configuration, whole-intent repair of parsed candidates, schema-6 persistence access, and graph-shaped revision prompts. The former contracts and tests were rewritten, with no compatibility adapter. There are no proof or semantic assessment phases. The normal path is coordinated in `TypedWorkflowPlanner.cs`, with construction and compilation in the two adjacent core files. See [architecture and public contracts](workflow-planning-v9.md).

Schema-7 storage uses new encrypted record namespaces and a new default database. Hosts retain EF Core indexes, public KeyVault encryption, tenant isolation, revision conflicts, durable request reservations/receipts and cumulative budgets. Default limits remain eight calls, two repairs and medium reasoning. Approval, saving and execution retain exact-artifact and current-contract/policy checks. Workflow approval and runtime publication confirmation remain separate.

## Frozen request-size comparison

Baseline planner: `95c3b30`; baseline measurement harness: `f71a731`. Frozen binaries were retained before implementation. Measurements include UTF-8 prompt and response-schema bytes and exclude provider framing. Baseline size capture used a local stdin/stdout stub and made **zero provider calls**. Candidate sizes come from the same eight requests in the offline runner, with identical configured limits. Correcting producer error metadata does not affect these card sizes.

| Case | Baseline bytes | Candidate bytes | Reduction |
| --- | ---: | ---: | ---: |
| local | 25,573 | 11,156 | 56.4% |
| read_transform | 25,604 | 11,187 | 56.3% |
| nullable_defaults | 25,702 | 11,285 | 56.1% |
| conditional | 25,660 | 11,243 | 56.2% |
| collections | 25,722 | 11,305 | 56.0% |
| protected_cleanup | 25,630 | 11,213 | 56.2% |
| review_french | 29,834 | 13,927 | 53.3% |
| review_distractors | 60,264 | 16,255 | 73.0% |

The large-catalog target of at least 50% reduction is met by this static measurement. Byte reduction is not a provider token-usage measurement. The runner also reports conservative estimated input tokens separately.

## Offline construction and execution

All eight fixed intent responses reached FinalReview on the first pass, with one interpretation call and zero repairs. Independent execution correctness is 8/8. Complex-case median and upper-quartile calls are both 1 (collections, protected cleanup, and the two review cases). Usage and costs are `null` for fixtures, not zero.

The corpus covers arithmetic, read/transform, nullable values/defaults, conditional routing, parallel iteration/subflows, protected writes/cleanup, the original French review request, and a paraphrase with 80 distracting tools. Nominal and alternate observations detect hard-coded results. The review oracle checks one clone, reuse of its returned directory, the exact review inputs, all four requested check outcomes with original evidence, stored-draft evaluation, cleanup, and publication gates. Passing, failed and incomplete verification require APPROVE, REQUEST_CHANGES and COMMENT respectively; rejection and changed head prevent publication and still clean up. No GitHub or real repository is contacted by these tests.

These fixtures prove engine behavior for those business plans. They do not measure the probability that a live model generates those plans. The requested 75% complex live-run target cannot be assessed from this evidence.

## Live evaluation and spending

The campaign used the configured KeyVault-backed OpenAI model `gpt-5.5-2026-04-24`, medium reasoning, 96,000 input and 32,768 output limits, eight calls and two repairs. The shared encrypted campaign ledger enforces a EUR 50 ceiling and prevents dispatch while an earlier request lacks a receipt.

| Baseline run | Outcome | Calls / repairs | Verified input / output tokens | Estimated cost EUR |
| --- | --- | --- | --- | ---: |
| local, repetition 1 | MODEL_OUTPUT_LIMIT | 1 / 0 | 5,387 / 32,768 | 0.88130454 |
| read_transform, repetition 1 | MODEL_DISPATCH_UNVERIFIABLE | 1 / 0 | unknown / unknown | unknown |

Neither run reached FinalReview. The second dispatch returned no durable completion receipt after approximately five minutes. Its charge is unknown, not zero. The ledger remains pending; it was not reset, replaced, or bypassed. The candidate has zero live runs. Planned coverage was three repetitions per case before and after (48 runs); observed coverage is two attempted baseline runs and one complete usage receipt. Known incremental spend is EUR 0.88130454 plus the unknown second request. The budget was not reported as exhausted; uncertainty stopped further dispatch.

The two earlier stopped schema-6 live sessions were left untouched. Their read-only accounting snapshot remains 7 calls / 2 repairs / EUR 6.16124902 and 4 calls / 2 repairs / EUR 1.42431408 respectively; these historical ledgers are separate from the evaluation campaign. No actual PR execution, publication, merge or deployment was performed.

The [redacted machine-readable measurements](benchmarks/business-intent-2026-09-19.json) retain failures and unknown usage explicitly. No credentials, model responses, private source or user database payloads are included.

## Validation and release checks

The corpus exposed and fixed numeric singleton array conversion in Jint, business failure observations being mistaken for transport failures, and a generated-scope naming collision. Direct regressions also preserve authoritative port constraints/defaults, including nested nullable output contracts. Existing Agent review-schema tests caught and verified the nullable conversion correction.

Verified on macOS arm64 with .NET SDK 10.0.300:

- Complete solution suite: **2,445 passed, zero failed, one environment-gated live test skipped** across 29 test projects.
- Release solution build with `-warnaserror`: zero warnings/errors.
- Release NuGet packages: Flow.Core, Flow.Planning and Flow.Integrations; all warning-free.
- Eight-case offline runner: 8/8 first-pass valid, 8/8 FinalReview, 8/8 independent execution; 60 isolated scenarios and 22 independent execution variants.
- Published Native AOT planner: all eight cases pass serialization/restart, planning, scenarios, approval and nominal independent execution; one call and zero repairs each.
- Native AOT audit with known-warning exceptions disabled: six warnings, all from Jint 4.16.0 CLR interop/default JSON conversion. The existing publish-local IL2026/IL2104/IL3053 exceptions are unchanged; no library analyzers were disabled. The normal Native AOT publish was warning-free.
- Published trimmed, self-contained Agent.Server: warning-free; a fresh temporary store passes encrypted schema-7 persistence, tenant isolation, revision conflict checks, review draft storage and uncertain-publication replay. Bundled tools/browser installation were excluded from this persistence-only publish.
- Agent.Server Vite frontend: frozen-lockfile install and production build passed without warnings. Razor components compile in the solution build.
- `git diff --check`: passed.

Reproduction commands (serialize builds to avoid sharing intermediate output concurrently):

```bash
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet build GnOuGo.Agent.sln -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Integrations -c Release -m:1 -warnaserror
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true \
  -m:1 -warnaserror -p:PublishTrimmed=true -p:PublishSingleFile=true -p:PublishAot=false \
  -p:SkipClientBuild=true -p:SkipBundledServerTools=true -p:SkipPlaywrightBrowserInstall=true
planning_smoke_dir=$(mktemp -d /tmp/gnougo-planning-persistence.XXXXXX)
src/GnOuGo.Agent.Server/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Agent.Server \
  --planning-persistence-smoke "$planning_smoke_dir"
corepack pnpm --dir src/GnOuGo.Agent.Server/ClientApp install --frozen-lockfile
corepack pnpm --dir src/GnOuGo.Agent.Server/ClientApp build
```

Commits were delivered incrementally on `feat/deterministic-planner-v2`: benchmark measurement (`f71a731`), business intent/construction and schema 7 (`a553b00`), shortlisting/local corrections (`ff83514`), then the final runtime regressions, evaluation artifacts and documentation. Each coherent implementation was tested and pushed without force.

## Remaining weaknesses

- Live-model reliability, repair quality and token/cost improvements need the unfinished paired evaluation; offline success cannot substitute for it.
- Text retrieval has no semantic guarantee. A missed capability can need an additional choice call; large eligible domains fail explicitly when they cannot fit the budget.
- Static expression inference deliberately covers a finite subset. New business shapes may require small type declarations or a correction; unknown or conflicting contracts fail closed.
- Revision import supports representable business operations and rejects arbitrary executor-specific handlers/functions instead of exposing a graph escape hatch.
- Approval currently hashes the retained catalog as well as policy. An unrelated contract change can conservatively invalidate approval.
- Isolated scenarios and fake review integrations cannot establish real package-manager, repository, service or publication behavior.
