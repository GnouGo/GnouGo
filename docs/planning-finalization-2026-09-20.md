# Compact planner merge finalization — 2026-09-20

The accepted planner behavior remains frozen at `65dc34a5184e1ab118c0edd7676f1b2c3dd23f45`. This pass starts from `9216cbc3f336c2eef4bd34ebcd89f0b1c644e304` and performs documentation cleanup and offline release verification only. There were no paid model calls, new benchmark samples, production planner changes, public API changes or storage migrations.

The [accepted live reliability report](planning-default-response-domains-2026-09-20.md) remains the live evidence. Documentation and release-tooling commits have not received another live-model evaluation.

## Architecture frozen for merge

`Prompt → compact WorkflowIntentPlan → capability resolution → deterministic PlanningGraphBuilder → validation/correction → PlanningGraphCompiler → scenarios → approval`

Read `TypedWorkflowPlanner.cs`, `PlanningGraphBuilder.cs` and `PlanningGraphCompiler.cs` in `src/GnOuGo.Flow.Planning` for the normal path. The model specifies business operations and data relationships. The builder owns known contracts, MCP envelopes, fixed arguments, transport results, scopes, confirmation and cleanup guards. Flow.Core owns provider-neutral contracts and execution without referencing another GnOuGo component; Planning remains independently publishable.

- Input defaults are recursive literals. JSON `default: null` means no default; explicit literal null must satisfy the declared or inferred contract. Invalid defaults are rejected, never rewritten silently.
- Cleanup's `after` relationships express ordering. Resource references, explicit conditions and permissions determine availability; failed acquisition does not create a resource.
- Local corrections use issued targets and complete deterministic revalidation. Zero/one/multiple candidate resolution, eight calls and two repairs remain unchanged.
- Schema-7 sessions and receipts use public encrypted KeyVault records with tenant-scoped EF Core/SQLite indexes, optimistic revisions and cumulative budgets. No old session is migrated.
- Final approval binds the reviewed artifact, contracts and host policy. Saving/execution revalidate them. Runtime effect confirmation and review publication confirmation remain separate; uncertain publication never resends automatically.
- AI.Core owns bounded transient HTTP retries. Durable planner restart replays original reservations and schemas. The benchmark's opt-in uncertain-generation recovery reserves possible usage and uses a fresh identity; it does not retry external effects or reset accounting.

See [the architecture and public contracts](workflow-planning-v2.md) for details.

## Review and cleanup findings

The review compared the complete branch diff with `main` at `776cbc977977b1c14393ec688fd029b81cf373c0`, including planner/runtime boundaries, HTTP retry ownership, persistence, approval/publication, injected integrations, Python changes and build tooling. No substantive correctness or security blocker was identified in this review. This is not a separate security certification.

| Area | Finding and disposition |
| --- | --- |
| Planner and runtime | No production branches keyed to frozen case names, PR URLs or benchmark wording were found. Preserve deterministic validation and static dataflow/artifact analysis. |
| Older-looking helpers | `PlanningValueProvenance` traces actual bindings and artifact relationships; the runtime's semantic validator checks executable dataflow. Both have active callers and are retained. Public error identifiers and stored-error formatting are retained in this API-frozen pass. |
| HTTP and durable recovery | Preserve a single transport retry owner, durable reservations/receipts, original-schema replay, uncertainty bounds, cancellation and exhaustion behavior. |
| Host persistence and authorization | Preserve encrypted records, tenant isolation, optimistic updates, current-contract/policy checks, trusted stored approval and host-owned publication. |
| Benchmark tooling | Preserve case selection, separate cohorts/revisions, offline execution, encrypted evidence inspection, read-only replay and independent business/safety oracles. Historical budget readers preserve previous charges and are not dead code. |
| Python | The deleted Python planner has no replacement legacy path. The Python runtime/CLI still execute saved workflows; current planning belongs to the .NET package and hosts. |
| Documentation and CI | Correct the active review README to schema 7, update current-report links, remove the CI path to a deleted document and remove obsolete preflight/repair error descriptions from runtime READMEs. Historical reports are retained. |
| macOS Native AOT | The full verifier reproduced two missing Clang module-cache warnings from the .NET 10 Apple crypto archive when publishing Flow CLI. Import the existing publish-local archive normalization target, already used by the planner smoke and two MCP publishes. No warning suppression or NuGet cache mutation was added. |
| Publish verifier | Isolate its schema-7 planning database, disable background planning during the health smoke and invoke the existing published persistence smoke. Previously the verifier did not explicitly isolate planning state. |

Cleanup commits:

- `dffa598` — frozen architecture/reliability documentation and stale CI path removal; 147 planner tests, eight offline cases and local documentation links passed.
- `889b5bc` — isolated published planning storage and existing persistence smoke; PowerShell parsing and 22 focused persistence/lifecycle/publication tests passed.
- `cacc20e` — remove obsolete runtime error descriptions; 853 .NET Flow tests and 281 Python core tests passed. Public constants and behavior are unchanged.
- `2aeec47` — apply the existing Darwin archive target to Flow CLI; warning-free Native AOT publish, published triage smoke and 853 Flow tests passed.

## Fresh offline release validation

Validation uses a detached temporary checkout, isolated package/publish exports and temporary persistence paths. No live host, PR execution or GitHub publication is part of these checks. The first release run used `889b5bc272c99173ca09394ed737a653ddeb3a8e`; its publish verifier stopped on the Flow CLI archive warnings. The complete release run was restarted on `2aeec47` after the focused fix. The first failure log was retained. Environment: macOS ARM64, .NET SDK 10.0.300, Python 3.12.11 and a temporary official PowerShell 7.6.6 runtime verified against its release SHA-256.

| Check | Result |
| --- | --- |
| Complete Release solution tests | 2,566 passed, zero failed across 29 test projects; one opt-in live GitHub test skipped. |
| Release solution and benchmark builds | Passed with zero warnings and errors. |
| Retry, cancellation, budgets, restart, approval and oracle regressions | Included in the solution suite, including 147 Planning tests, 220 AI.Core tests, 66 Integrations tests and 336 Agent.Server tests (14 campaign tests). |
| Eight offline benchmark cases | 8/8 FinalReview and independent correctness; 31/31 execution variants, 60 planner scenarios, zero safety violations. These are deterministic fixtures, not new live reliability samples. |
| Python components | Core 281/281 and CLI 23/23 passed. |
| Repository Python script tests | 4/4 passed. |
| Local packages | All 12 NuGet projects from the release workflow; Python core and CLI wheel/sdist pairs. Nothing published externally. |
| Explicit Native AOT planner | Publish and all eight smoke cases passed on `osx-arm64`. |
| Full publish verifier | Passed all eight frontends, 13 configured publish profiles and published-binary checks on `osx-arm64`; no unexpected diagnostics. |
| Published Agent schema-7 persistence | Passed encrypted sessions/review drafts, tenant isolation, reopen/restart, cumulative counters, stale revision rejection and uncertain-publication replay in new temporary stores. |

Commands, from the isolated checkout unless a component directory is specified:

```sh
dotnet test GnOuGo.Agent.sln -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
dotnet build GnOuGo.Agent.sln -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark
python3 -m unittest discover -s scripts/tests -v

# In each of librairies/python/gnougo-flow-core and gnougo-flow-cli:
uv run --extra dev pytest -q
uv build --out-dir "$validation_root/packages/python"

# For every project in .github/workflows/publish-flow-dotnet-nuget.yml's $projects:
dotnet pack "$project" -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true -o "$validation_root/packages/nuget"

dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -o "$validation_root/planner-aot"
"$validation_root/planner-aot/GnOuGo.Flow.Planning.Smoke"
KeyVault__DatabasePath="$validation_root/isolated-common-keyvault.db" \
  pwsh -NoProfile -File scripts/verify-warning-free-publishes.ps1 -RuntimeIdentifier osx-arm64
```

`validation_root` is a temporary export directory. `SkipModelMetadataGeneration=true` retains the checked-in metadata; the first run regenerated only its timestamp comment, which was restored in the temporary checkout. The PowerShell executable is temporary, not a global installation. The verifier uses frozen pnpm installs and builds all eight configured frontends before its 13 publish profiles; `SkipClientBuild=true` in individual publishes reuses those completed frontend builds. It also uses the existing bundled-tool/browser and published-binary checks. No new warning suppressions or relaxed checks were added. Files.Server emitted its two existing, exact-pinned Microsoft.EntityFrameworkCore.Tasks 10.0.11 experimental notices, as documented in its [README](../src/GnOuGo.Files.Server/README.md); they are not new diagnostics. Normal solution/benchmark builds and the explicit planner/Flow CLI Native AOT publishes had zero warnings.

The frontend list is Agent.Server, Assets.Animation.Server, Diff.Server, DocIngestor.Server, Files.Server, Flow.Server, KeyVault.Server and OtlpCollector.Server. The Native AOT profiles are Cmd.Mcp, Document.Mcp, Git.Mcp, GithubCopilot.Mcp, DocIngestor.Mcp, Flow.Cli, Files.Server and Assets.Animation.Server. The trimmed single-file profiles are Agent.Mcp, KeyVault.Mcp, OtlpCollector.Server, Agent.Server and Agent.Desktop.

## Reliability and unchanged accounting

| Accepted live cohort at `65dc34a` | Observed |
| --- | --- |
| Pilot | 7/7 FinalReview; 29/29 independent execution variants |
| Measured FinalReview | 20/21 (95.2%), gate at least 19/21 |
| Measured FinalReview within two physical calls | 18/21 (85.7%), gate at least 16/21 |
| Median / upper-quartile physical calls | 1 / 2 |
| First-pass validity | 12/21 (57.1%) |
| Approved independent execution | 85/85 variants passed |
| Safety violations | Zero |

Read-only campaign inspection reconfirmed the recorded evidence hashes unchanged:

- Current campaign `compact-intent-http-recovery-20260919-952f64a`: 123 reservations/receipts, 133 physical attempts; known EUR 4.0701919721 plus EUR 1.2766492147 reserved for old uncertainty, upper bound EUR 5.3468411867. Evidence hash `b46a0934298ac6288b6d3b7d7c3bda02b89d06daa0746b846e102a0f8b2cf47c`.
- Older independent campaign `compact-intent-candidate-20260919-2d15b1`: 14 reservations, 13 receipts, one unresolved pre-journal dispatch; known EUR 0.3672033159 plus unknown usage. Evidence hash `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`. The absent old usage is not zero and has no verified upper bound.

No ledger, stopped session, original schema or receipt was replaced. Inspection uses the benchmark's existing `--campaign <id> --inspect-campaign` command; no private reproduction content is committed.

## Limitations and merge status

First-pass validity is 12/21; one measured conditional run safely stopped on an unchanged invalid default repair and never produced an approved artifact. Provider latency and retries remain material. The live evidence covers one pinned model with mocked external integrations; actual PR execution and publication are untested here and remain outside this finalization. Static type inference and graph import deliberately reject unsupported constructs.

The complete local release suite passes, and no known technical merge blocker remains. At report preparation, CI on code revision `2aeec47` had passed planning validation, stable .NET/Python tests, Agent.Server tests, Linux Server, Windows KeyVault and Docker builds; the remaining platform packaging jobs were still running. These pending jobs are not claimed as completed. The documentation/report-only head is checked separately after pushing; consult [PR #99 checks](https://github.com/GnouGo/GnouGo/pull/99/checks) for its current results.

GitHub requires one approving review for [PR #99](https://github.com/GnouGo/GnouGo/pull/99) and currently has none. **Technically ready to merge; awaiting the required human approval.** No merge, auto-merge, history rewrite or branch-protection change was performed. Local packages and validation logs remain in the temporary release export, outside the repository; no credential, private response or database is included in this commit.
