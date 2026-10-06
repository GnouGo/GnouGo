# Approved Amazon execution: failed request admission, cleanup completed

The user explicitly approved revision **11**, artifact **`1557a5d6c72c4c30510b0b4e9b08050ae4b8d5c27dc0bd47545ac98d0df238e1`**, and all five reviewed requirements. The harness accepted that command, persisted approval at session revision 12 and executed **once** on frozen source/harness `7adab9ee`. The approved YAML is byte-for-byte unchanged. The failed run is retained and must not be replayed.

Run: `exportglue20261006a-amazon-1`, cohort `exportglue20261006a`, ten products maximum. [Original review](amazon-review.md) · [Explicit approval command](amazon-approval-r11.json) · [Result and durable receipt summary](amazon-execution.json).

## Result

**Execution and independent oracle failed.** Findings are `workflow_execution_failed` and `workbook_missing`. Search-page navigation and complete observation consumption succeeded. No consent click, product-page visit, product extraction or workbook write occurred.

The first consent `mapping.dynamic` stopped with `LLM_BUDGET_EXCEEDED` before provider dispatch. Its `consentPages` input contains the complete projected collection: **52 pages / 1,488 records / 207,455 serialized bytes**. The mapping returns one consent decision/object, so it is a global extraction binding; it is not an independent-item array mapping and cannot silently change semantics by sampling pages.

Read-only reconstruction with the frozen campaign estimator gives **97,193 estimated prompt tokens** and **181,033 estimated tokens for the complete serialized request** (325,336 bytes), exceeding the unchanged **96,000** admission allowance. These are conservative estimates, not billed tokens. The durable inner receipt records `LLM_BUDGET_EXCEEDED` with verified completion. Mapping telemetry records one local attempt and zero repairs; **zero mapping calls reached the paid provider**, no script was learned and no cache entry was created. This is a known pre-dispatch failure, not unknown external completion.

All manifest cursors were read in order. The producer reported `captureTruncated: false`, `manifestTruncated: false`, exactly 1,488 records, and every page had `captureTruncated: false`. Full observations remain encrypted. Public [observation evidence](amazon-observations.json) records counts, identities and response hashes. The consent view excludes selectors and links, but still carries every heading, link and text record; its repeated grouping strings alone contain 85,938 characters. Narrowing fields did not make this global request small enough.

## Cleanup and journal evidence

The compiler correction passed its live failure-path check: **1,169 normal steps / 10,000**, **one finalization step / 50**, finalization completed. The workflow itself called `browser_close` once and received a verified receipt with its literal description. The independent oracle then confirmed no active page and performed its own idempotent safety close; that second close is not counted as workflow cleanup.

| Current run measurement | Value |
| --- | ---: |
| Total durable invocations | 1,170 |
| `set` invocations | 1,056 |
| Finalization steps | 1 |
| Reconstructed logical journal JSON | 373,418,808 bytes |
| Small authoritative checkpoint JSON | 358,629 bytes |
| Referenced immutable blocks | 4,498 |
| Referenced block JSON | 11,853,658 bytes |
| Checkpoint inspection read | 46.98 ms |
| Full journal reconstruction | 6,252.44 ms |

[Journal measurements and request sizes](amazon-journal-measurements.json). Block sizes exclude superseded immutable records, encryption and SQLite overhead; they are not total physical database storage. Timings are read-only inspection, not checkpoint-write latency. The large logical journal and full reconstruction cost remain visible limitations. This run differs from the historical failure and is not a matched performance comparison. Pure record-copy loops had no per-record workflow invocation, but each observation page still incurred normal glue.

The read-only measurement uses public KeyVault APIs and verifies tenant/run ownership and block hashes. For reproduction, copy [the inspection source](journal-inspection.cs.txt) and [project](journal-inspection.csproj.txt) to a temporary directory, adjust only the frozen checkout path if needed, build with `dotnet build -warnaserror`, and run against the retained workspace. It does not dispatch inference or resume execution.

## Accounting and acceptance

| Stage | Planning | Execution |
| --- | ---: | ---: |
| Logical/provider attempts | 7 / 7 | 1 / 1 |
| Verified input/output tokens | 93,000 / 26,785 | 116 / 342 |
| Cost EUR | 1.125699 | 0.009619 |
| Active latency ms | 317,835 | 63,420 |

Execution's sole paid call constructed the search URL. Planning had two discovery reads, zero automatic repairs and four review revisions. Total reported latency is 381,255 ms. The cohort stays **0/6**, with this one failed execution and five unexecuted slots; no code-review run or expansion occurred.

Campaign upper bound: **EUR 91.906340 / 150**, including **EUR 2.606753** unchanged historical unknown reservations. No current-run usage is unknown. [Ledger](ledger-after-execution.json) · [Unchanged six-slot report](cohort-after-execution.json).

No production source, architecture, mapping behavior, permissions, token ceilings or execution oracle changed during this approved execution. PR #117 remains draft. The next generic correction should address the **composition**: use existing bounded independent extraction to build a small, observed candidate view before a global decision, preserving group context and complete source coverage. This needs deterministic regressions and a newly reviewed artifact, not replay or a higher limit. Neither the current compiler fix nor successful cleanup establishes end-to-end business success.

The latest CI snapshot for the evidence commit has 22 successful checks and a separate version-tag job failure: its API response was an object where `jq '.[-1].commit.message'` expected an array. The log does not establish why the API returned that object. No unit-test failure is reported; dependent packaging jobs are skipped. [CI snapshot](ci-after-execution.json) · [Failed job](https://github.com/GnouGo/GnouGo/actions/runs/37511942274/job/112438995315). This evidence-only turn did not rerun the already passing local 4,634-test solution gate or change CI policy.
