# Authorized fresh planning — blocked before execution

Frozen candidate: `7376b4bd713d72d43dbe62270e41823273e0e618`. Run: `physicaloutputs20261010a-amazon-1`, maximum ten products. The user authorized fresh paid planning; execution still required separate concrete artifact approval. No production code, frozen binary, permission, limit or oracle changed during this attempt.

## What ran

Configured pricing/currency readiness passed and every frozen source/binary hash matched. The campaign started at **€121.582372 / €150**. The fresh identity was absent before dispatch. Historical uncertain requests and reservations remained untouched.

The initial discovery/proposal reached revision 2 in two calls with zero compiler diagnostics. Review rejected its unrequested consent action, whole-snapshot extraction, selection hidden inside extraction, missing completeness assertions and formatter-authored completion flag. See `amazon-r2-findings.json`; this was never execution-approved.

An explicit review revision within the same planning session reached revision 4, still with zero automatic repairs. It removed consent work and introduced bounded page extraction and both ID lookups. Review still found learned copying of authoritative records, source-absent classification labels in product extraction, and incomplete local guards. A second limited revision requested an existing typed projection, observed fact arrays, exact original URLs and complete producer-metadata checks. Requirements remained unchanged. Both revision commands and superseded artifacts are retained.

The fourth model response completed and has a durable receipt. Its approximately **18 KB** proposal is retained in `pending-received-proposal.json`; its exact issued schema is in `pending-issued-schema.json`, captured read-only through public encrypted-storage APIs. **This proposal has not completed validation and is not an approved artifact.**

## Local validation blocker

The active thread repeatedly traversed:

```text
PlanningModelCalls.CallAsync
→ PlanningContractValidation.ValidateInstanceFindings
→ JsonSchemaInstanceValidator.ValidateInstanceNode
→ CountMatchingVariants / ValidateObject / ValidateArray
→ recursive child alternatives
```

See `validation-managed-stack.txt`. The process accumulated **10 minutes 23 seconds of CPU** in an **11 minute 21 second** revision invocation without reaching the next checkpoint. This was local response-schema validation, not Browser navigation or the previously corrected switch materialization. No `materialized_memory` failure was observed in this attempt because execution never started.

Source inspection shows that `CountMatchingVariants` recursively evaluates every alternative; incompatible literal tags inside object properties do not prevent other recursive properties being checked. Nested typed predicates amplify that work. The next minimal generic correction is to reject provably incompatible alternatives before descending, reuse existing discriminator/type checks, and avoid unnecessary `anyOf` traversal while retaining exact `oneOf` semantics and diagnostics. Replay this saved response against its original schema without inference, with positive/negative and nested-predicate regressions. No such production correction was made during this frozen live attempt.

After verifying all four durable receipts and zero execution, the owned harness was stopped. SIGINT/SIGTERM did not interrupt synchronous validation; only that verified process was force-stopped. Its persisted session was **not rewritten**: revision 5 still says `generating`, with the fourth pending request and a completed receipt, but no process is running and the current artifact hash is null. This is a committed model completion with unfinished local validation, not unknown external execution. No historical invocation was resumed or reclassified.

The last completed phase result still describes revision 4 and is stale. It records 491,965 ms; the interrupted revision adds at least 681,000 ms. Any later continuation must preserve this elapsed work, validate the original receipt/schema and reuse it without inference. Do not reset allowances or treat the stale result as current approval readiness.

## Accounting and outcomes

| Measure | Result |
| --- | ---: |
| Logical model calls / committed receipts | 4 / 4 |
| Physical transport attempts | 5 |
| Automatic repairs / explicit review revisions | 0 / 2 |
| Discovery reads | 2 |
| Verified input / output tokens | 58,273 / 20,341 |
| New known cost | €0.800067 |
| New unknown attempts | 0 |
| Campaign committed/reserved upper bound | €122.382439 / €150 |
| Historical unknown reservations retained | €6.501615 |
| Remaining campaign allowance | €27.617561 |
| Browser execution / product visits | Not started |
| XLSX creation / independent workbook oracle | Not run |
| Execution approval | Not requested for an unvalidated artifact |

Exact decimal values and unchanged historical reservations are in `ledger-before-planning.json`, `ledger-after-planning.json` and `live-planning-result.json`. All requests, receipts and review history remain encrypted in the campaign. Published evidence omits credentials and observation payloads.

The earlier full local solution validation remains **5,035 passed / 13 existing skips**. No production change required another solution run here. At the retained CI snapshot, the frozen candidate's main build had 20 successful jobs, four skips and its host-test job still running; the separate planner workflow was also running. These are pending CI results, not a claim of final green CI. Public job snapshots are retained separately.

PR #117 remains draft. Neither a successful planning response nor the offline physical-output fix establishes an Amazon-to-Excel execution result.

## Commands

Run from the frozen worktree with its original built benchmark. These document commands already issued; **do not rerun them to replay this session**.

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability plan --workspace /path/to/existing/encrypted/workspace --campaign schema-portability-20261002 --cohort physicaloutputs20261010a --run physicaloutputs20261010a-amazon-1 --case amazon --max-products 10
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability revise --workspace /path/to/existing/encrypted/workspace --campaign schema-portability-20261002 --cohort physicaloutputs20261010a --run physicaloutputs20261010a-amazon-1 --case amazon --max-products 10 --revision-command /path/to/revision-r2.json
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability revise --workspace /path/to/existing/encrypted/workspace --campaign schema-portability-20261002 --cohort physicaloutputs20261010a --run physicaloutputs20261010a-amazon-1 --case amazon --max-products 10 --revision-command /path/to/revision-r4.json
```

Read-only inspection remains available through `--schema-portability inspect-run` with the same workspace, campaign and run arguments. It does not resume inference or execution.
