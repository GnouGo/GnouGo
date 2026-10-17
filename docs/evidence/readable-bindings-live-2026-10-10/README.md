# Fresh readable-bindings Amazon validation

**Final result: workflow completed, E2E acceptance failed.** The run visited ten
product pages, created a real eleven-row XLSX (header plus ten products), and
closed Browser. The unchanged independent oracle rejected visit recognition
and unsupported workbook descriptions. This is not a successful E2E result.
No historical or newly uncertain invocation was replayed.

Frozen source/harness candidate `e18452151b12fc9e978afc6450a8ca495e57d5ef`.
The user authorized one fresh live validation after the offline correction.
Campaign `schema-portability-20261002`; fresh run
`readablebindings20261010a-amazon-1`; ten products maximum.

The full local solution passed 5,154 tests (13 existing skips), and the
published/AOT/recovery gates are recorded in the preceding
[offline evidence](../readable-checked-bindings-2026-10-10/README.md).
Remote CI was still running when authorized planning started. Before execution,
all 29 non-skipped checks on the frozen candidate passed; four publication jobs
were skipped. The explicit artifact approval was received and persisted before
execution. See [final pre-execution CI](ci-before-execution.json).

Provider/pricing/currency readiness passed with the pinned model and existing
96,000-token input allowance. A disposable local Browser/Document readiness
probe verified real navigation, XLSX contents and cleanup with zero inference.
The isolated frozen checkout built without warnings or errors. The ledger
before planning retained €127.99483540892342384306230182 / €150, including
€6.5016147947905255187922165013 of historical unknown reservations. All three
historical unresolved request identities already have retained-inconclusive
closures; none was resumed, reclassified or released.

## Planning and review result

The fresh session reached `final_review`, revision **7**, with no compiler
diagnostics. [Concrete review](amazon-review.md) and [unchanged issued YAML](amazon-r7.yaml)
identify approval artifact
`c8677c56bdd5b5a6924efb70670f500569d933e9dda17fc6f32485796b2e0998`.
The user explicitly approved this exact artifact and all five requirements.
The [submitted approval command](approved-execution-command.json) binds revision
7 and its hash; approval persisted as revision **8** with unchanged YAML/hash.
The workflow executed exactly once on 2026-10-11. Acknowledgments came from the
user; none was selected automatically.

Initial generation plus one automatic repair stopped at revision 3 because its
product output changed accepted nullability. Review also found unrequested
consent work and whole-observation extraction. An explicit structural revision
preserved requirements and business work, removed that extra work, and introduced
bounded page-based extraction, compact selection and two-stage ID lookup. It
reached review at revision 5, but still used learned mapping to copy original
action records and limited page candidates prematurely.

A second limited structural revision replaced only that copying task with an
existing typed projection, reconnected by the original `reference`, retained the
local `href != null` assertion and corrected the candidate declaration to the
existing 200-record page cap. The global ten-product bound is unchanged. All
accepted requirements are identical between revisions 5 and 7. The feedback also
requested explicit truncation-flag assertions; the response retained the existing
success/non-null snapshot assertions. Review verified that `observation_complete`
itself rejects capture/manifest truncation before producing a usable snapshot;
no completeness guarantee was inferred from a model-written boolean.

The intermediate proposals, exact feedback and diagnostics remain separate files;
all original requests and receipts remain in encrypted storage. No manual YAML
rewrite, production-code change, permission change or new repair mechanism was
used. The two revisions kept the same cumulative session allowance.

| Planning measurement | Result |
|---|---:|
| Logical calls / transport attempts | 5 / 6 |
| Automatic repairs / explicit revisions | 1 / 2 |
| Discovery reads | 2 |
| Verified input / output tokens | 60,871 / 19,543 |
| Cost | €0.7903496317330730322122637324 |
| Active planning milliseconds | 536,597 |
| New unknown attempts | 0 |

Campaign upper bound after planning:
**€128.78518504065649687527456556 / €150**. Historical unknown reservations
remain €6.5016147947905255187922165013. No additional budget or allowance was
granted or inferred. [Planning accounting](planning-r7-result.json) remains separate from the
subsequent [execution accounting](execution-result.json).

The artifact contains **17 TaskPlan tasks, 38 compiled steps, 18 sets and three
workflows**, 100,225 YAML bytes and a 483-character maximum line. There are zero
`checkedMapping` occurrences, three first-present selection helpers, and 17 steps
with literal expression contracts. These figures describe this new composition;
they are not an identical-plan comparison with a prior live workflow. The earlier
[offline v7/v8 comparison](../readable-checked-bindings-2026-10-10/README.md)
remains the controlled lowering comparison.

At review, [CI](ci-at-review.json) still had pending checks. Execution waited
for their completion and configured accounting readiness. It then used only
the approved artifact; no planning, source or oracle change occurred during
execution.

Raw requests, observations and receipts stay in the encrypted campaign/workflow
stores. Any inspection output is confined to an access-restricted temporary
directory. This cohort remains separate from all earlier failed runs. Its execution
acceptance is **failed**, and the other five slots remain unexecuted. PR #117
remains draft.


## Approved execution and independent result (2026-10-11)

[Readiness](execution-readiness.json) passed before dispatch with the same pinned
model, verified prices/quote, €150 ceiling and 96,000-token input allowance. The
initial Browser acquisition discarded a navigation-invalidated generation and
succeeded on attempt two. All ten subsequent product acquisitions succeeded on
their first attempt. Across search and product pages, **199 complete observation
pages / 7,287 records** were acquired. Same-generation status remained unknown
after some document changes; retained last-response status was 200. These are
reported separately, never promoted to a current-generation HTTP guarantee.

The real Document call wrote `products.xlsx` in
`workflows/schema-portability-20261002/readablebindings20261010a-amazon-1/`.
The file is 4,902 bytes, SHA-256
`ba32b993aaad352a27ffa26a87fc6227a0fbf80b24da59e893fa333e31d30a1c`.
Independent ZIP/OpenXML inspection found the same eleven rows and four columns
as the unchanged harness (`name`, `description`, `price`, `error`). No workbook
was created or edited outside the workflow. The workflow's Browser close
completed; the oracle's following read confirmed `No active page` before its
idempotent disposal close.

The [official oracle](execution-oracle.json) remains failed:

- `product_visits_do_not_match_rows`: three real, complete product visits used
  `/gp/aw/d/...`; the existing oracle only recognizes `/dp/` and `/gp/product/`.
  It therefore counts seven distinct product URLs against ten workbook rows.
- `workbook_value_not_supported_by_captured_product_page`: independently
  checking every captured URL form confirms exact normalized names and prices
  for all ten rows, but descriptions for products **4, 5, 7, 8 and 10** are
  assembled/reworded text rather than a contiguous observed description.
  Products 1–3 have supported values but are excluded by the URL matcher;
  products 6 and 9 satisfy the unchanged complete row check.

[Per-row diagnosis](workbook-audit.json) distinguishes these causes. The broader
URL inspection is diagnostic only: it neither changes the oracle nor converts
this run to a pass. The bounded TSV stage preserved the product record text
apart from whitespace normalization; the description mismatch was already
present in the product-assembly interpretation. The third record also carries
an observed report-problem control label as an `error`; its presence in source
is not evidence of an actual business failure. Source grounding and successful
schema checks alone do not establish semantic correctness.

The smallest next data correction is to preserve exact observed description
values through assembly, avoiding synthesis for fields that are only being
copied. URL recognition is a separate test-harness coverage finding, requiring
its own reviewed regression rather than a relaxed content check. No corrective
source change, second execution or new planning request was made in this turn.

## Runtime, durability and accounting

| Execution measurement | Result |
|---|---:|
| Runtime logical calls / HTTP attempts | 34 / 34 |
| Mapping calls / interpretation calls | 21 / 13 |
| Mapping invocations | 11 |
| Program repairs / targeted specializations | 8 / 2 |
| Cache hits | 0 |
| Mapping source items processed and validated | 199 / 199 |
| Largest interpretation request estimate | 10,894 tokens |
| Largest mapping request estimate | 45,604 tokens |
| Verified input / output tokens | 388,474 / 70,787 |
| New unknown attempts / new retained reservations | 0 / €0 |
| Execution cost | €3.6081107462951459756855089184 |
| Execution including oracle | 1,062,559 ms |
| Normal / finalization steps | 154 / 1 |
| Durable completed invocations | 155 |

The ninth product required eight mapping attempts (five program repairs and
two specializations); the eighth and tenth also needed program corrections.
[Retained diagnostics](mapping-repair-diagnostics.json) show invalid helper
signatures, unsupported operations and `optional` applied to constructed
containers. All mappings eventually validated their complete assigned
collections, and nothing was published partially.

[Sandbox telemetry](mapping-telemetry.json) retains per-invocation counters:
maximum allocated bytes 30,223,824, materialized bytes 6,034,583, statements
1,871 and active sandbox time 37.0076 ms. Each maximum is across invocations;
they are not one combined observation. The same 50,000,000-byte, 10,000-statement
and 5,000-ms mapping limits applied, without resetting repairs' allowances.

[Durable journal inspection](execution-durability.json) confirms schema 9,
revision 637, `completed`, completed finalization and all 155 invocations
committed with no terminal errors. No resumed engine or new external call was
used for inspection. [Storage measurements](journal-measurements.json) show a
37,777-byte checkpoint and 1,277 invocation-reachable immutable blocks occupying
8,480,899 bytes as encrypted-record plaintext. Their logical invocation content
is 617,466,872 bytes, including 561,497,225 snapshot bytes. This is not total
on-disk encrypted storage: event blocks, index and encryption overhead are
excluded. Logical sizes were computed by memoized traversal without expanding
snapshots; blocks were checked for tenant/run ownership and hash integrity.

The campaign upper bound is now
**€132.39329578695164285096007452 / €150**, leaving
**€17.60670421304835714903992548**. All historical unknown reservations remain
**€6.5016147947905255187922165013**; no closure, reservation or historical receipt
was reclassified. [Six-slot report](cohort-after-execution.json): **0/6 accepted**,
one failed completed execution and five unexecuted slots. No code-review or
cohort expansion occurred.

## Reproducible inspection

Run the following read-only commands from the frozen checkout; keep full
inspection output private because it includes captured observations:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --workspace "$WORKSPACE" --campaign schema-portability-20261002 --run readablebindings20261010a-amazon-1 > "$PRIVATE_EVIDENCE/execution-inspection.json"
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --workspace "$WORKSPACE" --campaign schema-portability-20261002 --cohort readablebindings20261010a > "$PRIVATE_EVIDENCE/cohort-after-execution.json"
```

[Journal inspection source](journal-inspection.cs) uses public encrypted-record
APIs, verifies immutable block ownership/hashes and performs no dispatch or
storage writes. Compile in a disposable .NET 10 project referencing KeyVault
Core; pass the workspace path and `--sizes`. The approved YAML, artifact review,
original requests and prior evidence remain unchanged. Production validation
is the already-green frozen candidate; this delivery changes evidence only.
