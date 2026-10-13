# One approved execution: passed

`initialcomposition20261013a-amazon-1`, reviewed revision **9**, artifact
`fc751e55508433f9e32a3851f64ef45b49a6502ea338a970dcf173b43821e4dd`, completed its
one authorized execution. The [explicit authorization](execution-authorization.json)
acknowledges all three requirements. Approval was recorded at session revision 10;
the approved YAML did not change. The frozen candidate remains `bd83e46e`.

## Runtime and independent business outcome

- Search acquired **54 complete pages / 1,568 records**. Ten actual product
  navigations acquired another **163 pages / 6,243 records**. All **217 pages /
  7,811 original records** remained available and untruncated.
- The frozen `observed-products-v2` [oracle passed](execution-oracle.json), with
  [ten correlated visits and exact cells](product-evidence.json).
- The actual workbook is at
  `/Users/a115vc/Desktop/GnOuGo/workflows/schema-portability-20261002/initialcomposition20261013a-amazon-1/products.xlsx`.
  It contains **11 rows**: one six-column header plus ten products. Its 6,112-byte
  SHA-256 is `0113e6137cba5b2994ef6bdd71774930c1c93a5b5bde27f28b0316362f1ba576`.
- A separate [ZIP/XML readback](inspect-workbook.py), using no inference or MCP
  calls, [also passed](workbook-verification.json). It checked all **30 selected
  name/description/price values** against the exact original record identified by
  each decision, offered-candidate membership, navigation arguments, row order,
  original URLs, producer statuses/errors, writer receipt and actual output path.
  It read both explicitly addressed and sequential XLSX cells; the workbook was
  never modified by inspection.
- All ten descriptions are complete original blocks. Characteristics without a
  heading are used for the first three products; none was paraphrased or omitted.
  [Exact cell evidence](exact-cell-evidence.json) retains the text. Prices retain
  observed formatting and any accompanying discount text. Titles also remain exact.
- The first three offered results used redirect URLs; they were retained rather
  than excluded by an added selection rule. Every navigation argument equals the
  corresponding original URL. Producer metadata supports the final document URLs;
  recognition does not rely on a product-path pattern.
- The workflow's Browser close succeeded. The oracle's subsequent current-page
  probe reported **No active page**, then performed its existing idempotent close.
  [Cleanup evidence](cleanup-verification.json) distinguishes these checks from
  the workflow's own close. There were **13 workflow MCP invocations** and **two
  oracle MCP checks**.

Runtime took **696,677 ms**; runtime plus oracle took **697,959 ms** (about 11m38s).
There was no runtime error, missing-description decision, invented value, additional
action or repeated execution. The real workbook remains in its approved location.

## Mapping and request measurements

The [raw mapping telemetry](mapping-telemetry.json), [request measurements](runtime-requests.json)
and [aggregate measurements](execution-measurements.json) separate inference from
deterministic execution and acceptance:

| Measurement | Observed result |
| --- | --- |
| Independent mapping invocations | 11: search plus ten products |
| Generic mapping generations | 11, one per invocation |
| Program repairs / specializations / cache hits | **0 / 0 / 0** |
| Processed and validated pages | **217 / 217** |
| Generation-example pages | 31; the other 186 still executed and validated |
| Compact candidates | 93 search candidates, 474 fact candidates |
| Global-decision provider input | At most **4,328 tokens**, no raw snapshots |
| Maximum provider input, all execution calls | **36,551 tokens**, allowance 96,000 |
| Serialized issued-request envelopes | 1,045–89,169 bytes; these are not token counts |
| Maximum mapping allocated / materialized bytes | **15,137,736 / 6,089,271** |
| Mapping memory ceiling | **50,000,000 bytes**, unchanged |
| Maximum mapping statements / sandbox time | **1,823 / 38.7489 ms** |
| Effective statement / time ceilings | **10,000 / 5,000 ms**, unchanged |

The telemetry field named `failure_groups` sums to 100. In this cold execution it
records **initial unmapped shape buckets before the generic candidate**, not 100
failed executions. Every invocation succeeded with its first generic script;
there were no repair or specialization calls. Empty page-level candidate arrays
remain valid examined pages; the global decisions received the complete resulting
candidate collections.

The 23 execution model calls comprise 11 mapping generations, ten product-fact
decisions, one product-ID decision and one bounded query-to-URL conversion. Typed
copying, strict lookup, row/header construction and status conversion used **zero
inference**. These are cold live measurements, not a warm-cache benchmark.

## Planning, receipts and accounting

The [planning chronology](README.md) remains a separate result: initial generation
failed, r5 dropped work and was rejected, r7 required four targeted corrections,
and r9 became reviewable. This live success does **not** establish reliable initial
model composition.

| Phase | Logical calls / physical attempts | Known input / output tokens | Known EUR cost |
| --- | --- | --- | --- |
| Planning, including three explicit revisions | 6 / 7 | 94,883 / 17,269 | **0.887653** |
| Approved execution | **23 / 23** | **265,434 / 34,698** | **2.117977** |

All [23 execution requests](execution-receipts.json) completed with one HTTP 200
attempt and a committed receipt. There was **no new uncertain execution attempt**.
The prior planning reservation of EUR 1.308506 and every historical reservation
remain intact. No old request was replayed, revised or closed during execution.

The exact [final accounting](execution-result.json) records campaign known cost
**EUR 134.91157921983235921310520650**, retained reservations
**EUR 14.643921991732868307771552628**, and upper bound
**EUR 149.55550121156522752087675913 / 200**. Approximately **EUR 50.444499** remains
after reservations. No ceiling or allowance was increased.

## Durability, compatibility and validation status

Read-only [journal inspection](journal-inspection.json) confirms terminal
`completed`, **174 invocations**, journal revision/event count **699**, and completed
finalization. It used public encrypted-storage APIs and did not resume the engine,
materialize historical before/after snapshots or redispatch work. The inspection's
zero-call counters describe that inspection, not the 23-call runtime above.

All [13 protected historical records](protected-after-execution.json) and
[709 frozen binaries](binary-verification-after-execution.json) remain unchanged.
Approved YAML, historical receipts, failed proposals and prior verdicts are retained.
This turn changed only execution evidence; no production code changed.

The frozen candidate had passed the full 5,502-test solution, 13 existing skips,
all 29 applicable CI checks, package/AOT/recovery/skill gates and local workbook
oracles before execution. The separate evidence-only head `aea8c73c` was observed
with **21 successful checks, eight in progress and three skips**; no failure was
reported at that observation. [Exact CI state](evidence-head-ci-observation.json)
is retained without calling unfinished checks passed. New evidence commits may
start their own CI; they do not replace the frozen execution candidate.

The single live business scenario has now passed. Broader first-generation model
reliability, future cache behavior and future sites are not proved by one success.
PR #117 remains draft; no further paid session or execution was launched.
