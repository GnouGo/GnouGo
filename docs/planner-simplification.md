# Business TaskPlan simplification

PR #117 now keeps `LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML`. New sessions request accepted business requirements, caller inputs and a TaskPlan. They no longer request a second description of operations/effects, placement, coverage or outcome-to-task evidence mappings.

## Responsibility changes

- Removed `PlanningOutcomeAnnotations`, `PlanningOutcomeCoverage` and `PlanningOutcomeValidation` (393 lines), their prompt/schema sections, outcome-mapping repairs and versions 1–6 of active repair dispatch.
- Merged export repair into `TaskPlanRevisions`; shared built-in agent literal/workspace restrictions through existing compiler helpers. No new service, representation or planning phase. Production planning C# has a net reduction of 538 lines (204 added, 742 removed).
- The compiler reuses complete, equivalent existing export chains. It does not invent branch values, defaults or missing exports, flatten collections, or cross group/sibling boundaries. Missing chains remain narrowly diagnosed repair slots. Complete patches are checked atomically before changing a session.
- Inspected contract admission, typed argument shapes, fixed-input ownership, policy checks and compatible repair sources apply independently of historical outcome versions. Envelope 7 fingerprints all edit authority.
- MCP schemas and metadata continue to own effects, provenance, capabilities and permissions. No producer-specific rules or new mandatory metadata. Unknown effects use the existing conservative confirmation policy. Opaque results stay opaque. Cleanup remains explicit; a lifecycle label does not grant deletion authority.
- Review presents accepted requirements alongside business tasks and derives technical information from compiled operations. Neither compilation nor review proves arbitrary prose was fully implemented; independent execution oracles remain necessary.

The reference plan has four tasks: search, foreach, a value task selecting fields, and writing rows. This requires contracts that actually expose search results and row writing. Primitive browsing or document-encoding contracts can require additional explicit steps. The local four-task regression uses an MCP fixture whose writer owns row-to-TSV conversion and invokes the actual Document writer; it does not claim that every document MCP accepts rows natively.

## Compatibility and rollout

`IntentVersion=2` selects the simplified profile. TaskPlan, PlanningGraph and storage format 10 are unchanged. Historical annotation fields remain readable and keep their historical hash representation; only the new intent profile is added to new artifact hashes. Historical evidence and the 33/33 campaign are unchanged.

Unfinished legacy sessions report `PLANNING_REVISION_REQUIRED`. Explicit revision retains their baseline, receipts/history and cumulative budgets, clears generated artifacts/approval and creates fresh requests. A completed pending request must first satisfy its original identity, schema and durable accounting. Its old proposal is not applied or dispatched again. Unknown completion remains blocked for reconciliation. Designer offers settlement of an available response and the existing revision control. Other hosts receive the same revision diagnostic. Exhausted budgets require a new session. Already-approved artifacts remain approved; legacy unapproved artifacts must be regenerated and approved.

Deploy/restart the updated host, then use a new session or explicitly revise an unfinished session. This change does not edit or rerun saved workflows, replenish budgets, change runtime permissions or auto-approve execution.

## Deterministic evidence

The [frozen measurement harness and manifest](evidence/planner-simplification/manifest.json) compare baseline `fa02b055cce700e8df02f242de0e1c773fef8401` with this correction. Five unchanged corpus scenarios run three times each, after one warmup per scenario. Both cohorts execute real Flow with mocked MCP integration and independent corpus oracles; they use no provider. See [before](evidence/planner-simplification/before.json) and [after](evidence/planner-simplification/after.json).

| Metric | Before | After |
| --- | ---: | ---: |
| Execution oracles | 15/15 | 15/15 |
| Schema bytes per request | 18,302 | 16,863 (−7.9%) |
| Estimated input tokens, median / p95 | 8,005 / 8,058 | 7,281 / 7,334 (about −9%) |
| Planning calls per run | 1 | 1 |
| Repairs per run | 0 | 0 |
| Discovery page reads per run | 1 | 1 |
| Planning latency, median / p95 (ms) | 27.00 / 38.03 | 28.58 / 35.38 |
| Execution latency, median / p95 (ms) | 2.76 / 8.54 | 3.34 / 11.18 |
| Verified provider input/output tokens | unknown | unknown |
| Paid calls / cost | 0 / €0 | 0 / €0 |

Latency is recorded separately in both JSON files; it measures a deterministic adapter on this machine, not provider latency. The comparison command prints medians and nearest-rank p95. The sub-40 ms timings are noisy, and the final collection overlapped other local checks; no latency improvement is claimed. These measurements assume contracts are available and do not claim universally fewer discovery calls.

Three large retained fixtures now explicitly inspect all selected contracts and declare a 96,000-token ceiling for their separately identified fresh proposals (two previously used 24,000; the enum fixture also needs the additional inspected contract). The catalog-owned binding fixture uses 32,768. Their historical issued requests and limits are unchanged. Current typed repairs use about 15,654–15,935 estimated tokens versus 26,390–26,671 historically: roughly 40% smaller, not the former untyped test's 50% target. Typed contracts and complete authority are preserved instead of promising a universal reduction.

Execution coverage is distinct from those measurements:

- Six new four-task cases use real Flow and actual Document writing. XLSX names, prices, Unicode text, ordering, empty rows and output paths are inspected independently. Sequential/parallel iteration, missing required fields and denied approval are covered; failed/denied runs must not write a workbook.
- Existing local-product tests invoke actual Browser and Document MCP components on a disposable HTTP product site. Multiple/empty/missing products, navigation failure, denied writing and collection limits retain their execution oracles and cleanup checks.
- Nested export regressions execute both conditional branches through real Flow, preserving explicit alternate values and the unchanged TaskPlan. Existing scope/loop/cleanup, malformed-contract, cycles, agent-scope and unauthorized-repair tests remain.
- Placeholder-work regressions remain execution-oracle failures; there is intentionally no substitute natural-language completeness classifier.
- Encrypted receipt recovery, stale requests, tenant ownership, accounting and historical approvals are tested. The isolated browser checks cover legacy revision gating, explicit clarification, free text, keyboard access, mobile layout and reload.

No paid evaluation, external repository execution or real Amazon browsing was performed. Historical live failures are retained and are not reclassified as successes by these deterministic results.

## Reproduction

Run from the repository root. The harness is fixed outside the source checkout; `PlannerSourceRoot` points at an isolated baseline or candidate. Copy/build it separately for each cohort to avoid shared build output. Do not run the paid benchmark campaign.

```sh
git worktree add --detach /tmp/planner-baseline fa02b055cce700e8df02f242de0e1c773fef8401
cp -R docs/evidence/planner-simplification/harness /tmp/planner-baseline-harness
dotnet run --project /tmp/planner-baseline-harness/Measurements.csproj -p:PlannerSourceRoot=/tmp/planner-baseline --no-launch-profile > /tmp/planner-before.raw
```

```sh
dotnet run --project docs/evidence/planner-simplification/harness/Measurements.csproj --no-launch-profile > /tmp/planner-after.raw
python3 docs/evidence/planner-simplification/compare.py

dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
pnpm --dir src/GnOuGo.Agent.Server/ClientApp run build
PLAYWRIGHT_MODULE_PATH=/absolute/path/to/playwright/index.mjs dotnet test tests/GnOuGo.Agent.Server.Tests --filter FullyQualifiedName~PlanningClarificationBrowserTests -warnaserror

dotnet pack src/GnOuGo.Flow.Core -c Release -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -p:PublishAot=true -warnaserror -o /tmp/planning-smoke
/tmp/planning-smoke/GnOuGo.Flow.Planning.Smoke

dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true -warnaserror -o /tmp/planning-host
/tmp/planning-host/GnOuGo.Agent.Server --planning-persistence-smoke /tmp/planning-storage-smoke
```

The harness prints its measurements as the final JSON line; build output may precede it. The comparison script checks the frozen corpus/harness hashes and complete scenario/repetition identities before comparing retained cohorts. Use the platform's supported RID for publish checks. The existing CI planning workflow additionally checks Linux packages and published MCP contracts.

Final validation on production commit `61b550ef903ae89f9f42bda2db5ab28441140a66`: **4,042 solution tests passed**, zero failures, 12 platform/opt-in skips, with `-warnaserror`. This includes 777 planner tests and 593 Agent.Server tests with the browser enabled. Five independent Flow packages, the benchmark host build, planning Native AOT, the frontend build, published Cmd checks (54), published Git discovery (1) and trimmed-host encrypted recovery passed. Per-project counts, skips and environment are in the [manifest](evidence/planner-simplification/manifest.json).

The lower total than baseline reflects deletion of obsolete outcome-proof tests; their placeholder execution oracles remain. During migration, stale host replay expectations and two internal smoke/reflection call sites failed and were updated for the explicit revision profile. One earlier full run also hit a Copilot temporary-directory cleanup race; subsequent complete runs passed without changing Copilot runtime code. Linux/Windows publication and remote CI remain separate from these macOS ARM64 results.
