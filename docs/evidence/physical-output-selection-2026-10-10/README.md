# Physical output selection — PR #117

Candidate: `7376b4bd` (parent `22a809e6`). No paid planning, provider inference or historical workflow resumption was performed for this correction. Local fixtures perform real localhost Browser/Document work with deterministic inference. The historical failed run remains failed; its approval, journal revision 61 and encrypted planning record remain unchanged. PR #117 remains draft.

## Reproduction and correction

The retained expression in `businesstasks20261009a-amazon-1` reconstructs logical switch keys before selecting a boolean. Its envelope contains eight snapshot copies. Replaying that expression alone from the public encrypted journal APIs reproduced `CONTRACT_UNSATISFIED: materialized_memory`, with **58,751,376 materialized bytes** against the nested **50,000,000-byte** ceiling. The former diagnostic showed the shared 1,000,000,000-byte allowance.

The compiler now resolves eligible selection paths against the original producer, before container reconstruction. It retains the existing checked first-present selection and all intermediate schemas. A present null or invalid value does not select another alternative. Whole-container consumers still reconstruct logical keys. No result envelope, journal layout, executor, mapping semantics or enforcement limit changed.

Fresh compilation uses approval-fingerprinted `compact-bindings-v6`. Omitted/v1–v5 requests retain their lowering; v5 reproduces the historical approved YAML byte-for-byte. The public compiler overloads retain their behavior.

The corrected expression evaluated against the same saved context passes its boolean and `enum: [true]` checks and returns all **54 original pages**, deep-equal to the observed source. It does not resume the workflow. The original context, persisted journal and persisted planning session were compared before/after and remain unchanged.

| Same retained TaskPlan | v5 | v6 |
|---|---:|---:|
| YAML bytes | 79,833 | 75,119 |
| Failing step's expression bytes | 3,007 | 1,748 |
| Inference / external actions during replay | 0 / 0 | 0 / 0 |

The generic fixture contains arbitrary producer/field names and eight unrelated observations. Scalar evaluation allocates **10,304 bytes** and returns 14 serialized bytes at each unrelated-payload size: 0, 1,600,000 and 10,000,000 characters. Expression bytes fall from 792 to 213; YAML from 2,106 to 1,505. This measures the scalar path, not the cost of reading or reconstructing the full journal.

Memory diagnostics now report the enforced nested ceiling in `sandbox.memory_limit_bytes` and in the message; `shared_memory_limit_bytes` retains the outer allowance and `enforcement_scope: nested` identifies nested enforcement. Import, output materialization and Jint allocation tests preserve counters, nonretryability, cancellation and original compiler locations.

## Read-only reproduction

`retained-expression-before.json`, `retained-expression-after.json` and `measurements.json` contain sanitized results, not observations. The `.cs.txt` replay loads existing records using public encrypted-storage APIs, verifies historical approval, and evaluates only expressions. It never creates an engine, calls a provider/MCP or saves a record.

To repeat locally with access to the retained encrypted workspace:

```sh
replay_dir=$(mktemp -d)
cp docs/evidence/physical-output-selection-2026-10-10/read-only-expression-replay.cs.txt "$replay_dir/Program.cs"
cp docs/evidence/physical-output-selection-2026-10-10/read-only-expression-replay.csproj.txt "$replay_dir/replay.csproj"
dotnet run --project "$replay_dir/replay.csproj" -warnaserror -p:SkipModelMetadataGeneration=true -p:RepositoryRoot="$PWD" -- /path/to/existing/encrypted/workspace
```

The temporary project uses the existing benchmark friend assembly name to access the private compilation profiles; this adds no production entrypoint.

## Validation

Focused regressions: **16 compiler + 4 sandbox tests pass**. They cover first-present paths, nested scopes, both alternatives, missing/null/invalid values, whole-container logical keys, runtime assertion ordering, zero downstream dispatch on rejection, cleanup and receipt recovery without another call. Historical profile routing and existing approval-invalidation tests remain enforced.

The full solution passed **5,035 tests with zero failures and 13 existing skips**, across 33 projects. The host suite passed 739 tests (one existing skip), including all 51 local Browser/Document scenario variants and their independent workbook/visit/cleanup checks, including expected-failure oracles. Five Flow Release packages, the macOS ARM64 planning Native AOT smoke, published trimmed encrypted recovery, frozen benchmark build and skill validation all passed. See `full-solution.json` and `validation.json`. Builds use `-warnaserror`; `SkipModelMetadataGeneration=true` keeps the frozen metadata file unchanged rather than contacting/regenerating it during builds.

## CI baseline, separate from this correction

At inspected head `22a809e6`, [planner CI run 38011424415](https://github.com/GnouGo/GnouGo/actions/runs/38011424415) failed six retained-composition host cases (`fabricated`, `typed-repair`, `typed-lookup-repair`, `repeated-selection`, `consent`, `absent`). Their existing two-minute cumulative LLM budget expired during slow deterministic planning (`LLM_BUDGET_EXCEEDED` before mapping inference). No timeout, assertion or budget was changed here. New CI results must be reported separately; a local pass is not CI acceptance.

## Next live: prepared, not authorized yet

Rechecked campaign `schema-portability-20261002`: **€121.582372 committed/reserved of €150**, leaving approximately **€28.417628**. The five unknown transport attempts and three retained inconclusive logical requests remain reserved. Configured pricing/currency readiness passes with zero inference; effective planning/mapping input allowances remain 96,000 tokens.

Prepare `physicaloutputs20261010a-amazon-1`, maximum ten products, with the unchanged prompt, permissions and workbook/visit/cleanup oracles. Frozen hashes and identity checks are recorded in `next-live.json`. No new paid planning call or execution is authorized by this correction. Obtain new GO before planning, then concrete revision/hash-bound artifact approval and explicit requirement acknowledgments before executing once. Never reuse the historical approval or replay uncertain work.

The deterministic expression fix is proven. Fresh live planning, Amazon product visits, extraction, XLSX production and independent live oracles remain **unexecuted**.

After new GO, run planning from the clean frozen worktree (the command below is **not yet authorized or executed**):

```sh
cd /private/tmp/gnougo-physicaloutputs-candidate
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability plan --workspace /path/to/existing/encrypted/workspace --campaign schema-portability-20261002 --cohort physicaloutputs20261010a --run physicaloutputs20261010a-amazon-1 --case amazon --max-products 10
```

Recheck pricing/currency admission and frozen hashes immediately before dispatch. Review the resulting requirements and artifact; an execution command must use the new revision/hash-bound review file. The existing harness refuses to replay a run whose execution has already started.
