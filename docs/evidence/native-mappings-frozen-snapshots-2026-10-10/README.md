# Native deterministic mappings and frozen journal snapshots

Candidate: `6d2c65c4ecb329cc6da00e2b6b7b05ef930eb0d6`, PR #117 (draft).
The retained `physicaloutputs20261010a-amazon-1` execution is unchanged and must
not be resumed. Its [failure evidence](../recovered-amazon-execution-2026-10-10/README.md)
remains separate. All reproduction and measurement work below uses zero inference.

## Implementation

Fresh `compact-bindings-v7` compiles eligible single-path selections to property
access and per-item selections to native `map`. First-present alternatives,
strict lookup and ordered intermediate validation keep checked helpers. The
structural evaluator preserves missing-property failures, explicit nulls and
exact copied JSON values under the same limits. Compiler-owned constant programs
use escaped JavaScript template literals in multiline YAML. Learned mappings are
unchanged. Omitted/v1–v6 compilation remains supported and approval-fingerprinted.

Split journal encoding now probes inline size with bounded traversal, recursively
encodes containers, and serializes metadata separately from payload references.
Invocation snapshots remain backed by verified immutable blocks during normal
owner operations and recovery. Optional lease hooks retain clone semantics for
custom and historical monolithic stores. Reuse compares actual values against
frozen blocks; it does not trust mutable node identity. Changed values, removals,
object order and numeric representations are restored exactly. Public snapshot
access still yields detached editable values and schema-9 inspection remains
unchanged. Blocks precede the atomic checkpoint; uncertain completion still
prohibits replay and cleanup.

## Same-plan compilation measurements

The existing read-only `replay-compile --compare-bindings` command compiled the
retained TaskPlan with its original catalog. No saved request, approval or journal
was changed. v6 reproduced the saved YAML byte-for-byte.

| Measure | v6 | v7 |
| --- | ---: | ---: |
| Compiled steps | 106 | 106 |
| Sets | 71 | 71 |
| Switches | 16 | 16 |
| `m.select` calls | 56 | 19 |
| YAML bytes | 111,417 | 115,474 |
| YAML newline count | 2,497 | 2,961 |

This is a syntax/readability reduction, not a claim of fewer execution steps.
Readable formatting adds 3.6% to YAML bytes. Remaining branches and helpers retain
their existing checks and independently observable boundaries. See
[exact profile hashes and measurements](compilation-measurements.json).

## Snapshot regression and measurements

The disposable fixture has 52 pages and 1,501 records, roughly 1.25 MB of unique
observations and eight repeated copies in its execution envelope. The base snapshot
is 9,987,879 logical bytes; 12 before/after pairs nominally represent 239,709,096
logical bytes, excluding the small changing iteration field. A changing scalar prevents a trivial unchanged-root shortcut.

The fixture checks bounded checkpoint allocations, no repeated unchanged payload
writes, deferred historical snapshots, one read per unique block on restart,
mutation isolation, exact numeric/null restoration and public full-save edits.
Additional tests inject block/checkpoint failures and verify committed-receipt
recovery without duplicate effects. A property-order regression was first
observed failing and then corrected without cloning verified subtrees.

The [isolated snapshot measurement](snapshot-measurements.json) wrote 150 unique
blocks and 1,305,055 envelope bytes. Warm two-snapshot capture allocated a median
12.56 MB; checkpointing allocated a median 274.7 KB, wrote 2.2–3.0 KB and took a
median 8.03 ms. Process peak memory footprint was 111.12 MB (maximum RSS 206.09 MB).
These are disposable-fixture measurements, not estimates of an entire live run.

A separate [public-API restore probe](measure-restore.cs) measured restart without
expanding history and exact restoration into a new execution object. Comparing
parsed JSON strings without decoding them repeatedly reduced the first warm
restore from 36.25 MB / 50.97 ms to 16.85 MB / 25.37 ms. The corrected build loaded
the journal in 228.27 ms / 18.06 MB and restored the selected state in 5.70 ms /
1.89 MB. A separate run of that probe occupied 2,088,960 bytes across its four
vault/index/ownership files. This is distinct from serialized envelope bytes.
See [restore measurements](restore-measurements.json). Checks still read
live values to detect arbitrary custom-executor mutation; the optimization is
not permission to trust mutable identity. Timing is environment-dependent.

## Validation

The focused persistence suite passes all 20 tests, including arbitrary executor
mutation, exact restore ordering, failure injection and receipt replay. Core,
Planning and Persistence Release packages pass. Planning Native AOT smoke passes,
including execution of the v7 multiline program. Published Native AOT Flow CLI
and Server pass encrypted restart recovery, tenant isolation, index rebuilding,
human answers and exact retained results. Published trimmed Agent.Server passes
its format-10 planning/schema-9 execution persistence smoke. Skill validation
passes.

The publish log contains only the two pre-existing allowlisted EF Core Tasks
experimental notices for CLI/Server; no warning suppression or runtime limit was
added. Preliminary validation runs superseded by later source revisions were
stopped and are not counted as complete solution passes. The final uninterrupted solution run on `6d2c65c4` passed **5,072 tests across
33 assemblies**, with 13 existing platform/live skips and zero failures under
`-warnaserror`. This includes 1,229 Planning, 1,187 Flow, 20 Persistence and 750
Agent.Server passes. See [per-assembly totals](solution-tests.json).
`SkipModelMetadataGeneration=true` disables network metadata generation, not tests.
All **29 non-skipped CI checks** pass on the frozen candidate; see [CI results](ci-validation.json).

No paid work was dispatched during implementation or measurement.

## Accounting and next live gate

[Configured readiness](readiness.json) resolved the exact deployment and a fresh
enough ECB quote with zero model calls. The request allowance remains 96,000
tokens. [Accounting](accounting-before.json) retains an upper bound of
€122.589844/€150, including €6.501615 of unknown transport reservations. All three
historical unresolved logical requests remain retained as inconclusive.

One new Amazon run, at most ten products, is authorized only after local and CI
validation pass. Its generated artifact still needs its own revision/hash-bound
approval and explicit requirement acknowledgments. No historical invocation is
replayed and no cohort expansion is included.

## Reproduction commands

Run the retained compilation comparison read-only (no execution or inference):

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability replay-compile --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --run physicaloutputs20261010a-amazon-1 --compare-bindings
```

The disposable snapshot fixture and complete solution use the existing checks:

```sh
dotnet test tests/GnOuGo.Flow.Persistence.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

The fresh live uses a clean detached checkout of the frozen candidate. Its
[build fingerprints](frozen-build.json) identify the binaries and harness.

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability plan --workspace /Users/a115vc/Desktop/GnOuGo --campaign schema-portability-20261002 --cohort nativesnapshots20261010a --case amazon --run nativesnapshots20261010a-amazon-1 --max-products 10
```

This command creates a new retained run. It is evidence of this invocation,
not an instruction to replay or overwrite it.

## Fresh live outcome

After all local and CI gates passed, the authorized fresh session
`nativesnapshots20261010a-amazon-1` stopped during generation: four logical calls,
five physical attempts, two repairs, two discovery reads, 29,043 input / 10,987
output tokens and **€0.421355** measured cost. The retained proposal has invalid
`each`, `choice` and `flatten` bindings; review also identifies broad interpretation
inputs and unrequested work. See [the detailed review](amazon-review.md).

No compiled YAML or approvable artifact exists. **Execution did not start; there
were no product visits or XLSX, and the live oracles remain unverified.** No further
paid revision, code-review run or cohort expansion was dispatched. Campaign upper
bound is **€123.011199/€150**, retaining all historical unknown reservations.
Production validation and this failed planning attempt are separate evidence.
PR #117 remains draft.
