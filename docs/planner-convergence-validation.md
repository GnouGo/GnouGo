# Planner convergence validation — 2026-09-11

Branch: `feat/deterministic-planner-v2`. The live prerequisite and release checks passed.

## KeyVault-backed Agent.Server live test

The configured `gpt-5.5-2026-04-24` model generated three workflows through
Agent.Server using the default 12,000 input / 8,192 output ceilings, concurrency four,
and five repairs per workflow/gate. The session reached final approval with no
remaining diagnostics. Both business review and artifact approval targeted their
exact persisted revisions.

The test classifies records in `classify_record`, aggregates their results in
`summarize_batch`, and orchestrates both calls plus an always-run finalizer in `main`.
The intent explicitly identifies classification, aggregation, and finalization as
the business operations; loops and calls are orchestration. Independent execution
checked mixed, empty, threshold-boundary, all-rejected, omitted-default, and invalid
inputs. Direct callee tests checked contracts and preservation of original record
fields. An injected call failure preserved its original error and ran the finalizer
exactly once. All cases passed.

The stored YAML exactly matched deterministic lowering. Approved artifact SHA-256:
`8cff69a96dc659880e6866068c3f012474dfd3a1e5e5b1bd717da65165400a2c`.

| Evidence | Result |
| --- | ---: |
| Durable requests with completed receipts | 24 / 24 |
| Construction requests, all assignments only | 15 |
| Maximum estimated input | 7,691 / 12,000 tokens |
| Output ceiling on every request | 8,192 tokens |
| Active holes, excluding superseded containers | 29 |
| Deterministically resolved holes | 7 |
| Distinct holes sent to the model | 22 |
| Total hole request exposures | 26 |
| Capability-matching model calls | 0 |

Direct-binding candidate counts were zero for 19 active holes, one for eight, and
two for two. Computation-parameter counts were zero for 13 holes, one for six, two
for six, three for two, and four for two. These are separate domains: a computation
parameter need not have its result's type.

Persisted gate accounting retained one classifier response failure/retry, one main
semantic repair, and one plan-level assessment-contract repair. Historical semantic
failure counts remained two for main and one for each callee. The classifier's first
response used its entire output ceiling for reasoning and returned no JSON; its
completed receipt permitted a bounded retry. The sibling schema candidate was
retained and validated. Later construction requests were typically 900–1,300
estimated input tokens. Callers waited for validated callee contracts.

Semantic review initially confused a generic native `set` definition with the
business output contract. Review now receives the graph's typed local value
contracts and cannot challenge a local executor through a capability-preparation
target. Invalid assessment targets can be reassessed only against unchanged graph,
behavior, contract, and fixture fingerprints; budgets and prior findings remain
in history. Independent execution and the subsequent review used the same artifact
hash.

## Reported input-limit failure

The saved `CodeReview` session reproduced a 26,143-token capability-selection
request. Selection now sizes the complete prompt and response schema into pages:
11,883, 11,912, 11,984, and 2,597 estimated tokens. Matching and matching repair also
previously sent the whole catalog, exceeding 29,000 tokens. They now use each
operation's recorded candidates, declared prerequisites, relevant dependency
context, and scoped response domains. Completed scopes are checkpointed and
validated on replay. The unfiltered-catalog repair fallback was removed.

Live selection, matching, and matching repair completed below the configured input
ceiling. This particular business session remains paused on clone-source and
conditional-publication contract findings; it has not produced an approved workflow.
No external business workflow was executed or GitHub feedback published.

Earlier exploratory sessions remain retained, including unverifiable provider
failures and a behavior revision that stopped on repeated candidates after
capability ownership changed. Unverifiable requests were not redispatched; their
budgets were not reset. They are not counted as successful live evidence.

## Build and test checks

Commands use `-warnaserror`; no new warning suppression was added. Model metadata
regeneration was skipped because its inputs are unchanged. Frontend builds run
separately from .NET checks.

| Check | Result |
| --- | --- |
| Solution tests, 28 test projects | 2,768 passed; none skipped |
| Release solution build | Passed, zero warnings/errors |
| Agent.Server frontend | Passed, no warnings |
| Planning component tests (included above) | 440 passed |
| Flow.Core component tests (included above) | 836 passed |
| Flow.Integrations component tests (included above) | 63 passed |
| Agent.Server planning-filter tests | 45 passed |
| Component packages: Flow.Core, Flow.Planning, Flow.Integrations, Agent.Shared | Passed, including packaged READMEs |
| Published macOS ARM64 Native AOT planning/persistence smoke | Passed |
| Published macOS ARM64 trimmed Agent.Server persistence smoke | Passed, including full bundled-tool publication |

The published checks retain the repository's existing documented Jint, Blazor and
EF Core trimming boundaries. No new analyzer suppression was introduced. The
workspace explicitly permits the pinned `@parcel/watcher` install hook, which only
builds native code when source compilation is requested, removing pnpm's unresolved
build-script-policy notice. Component packages include their READMEs.
