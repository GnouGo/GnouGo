# Amazon cursor validation and collection amplification

Frozen source/harness: `26cf8c63101f456f6409c8891072d2d5fdca7a01`. Cohort `cursorlive20261006a`, run `cursorlive20261006a-amazon-1`, reviewed revision 8, artifact `ef62b18563a5b87fb1172a449397a0464e06aff57abe6014b7d5239e845865c4`. The explicitly approved artifact executed once; this identity must not be replayed.

The Browser correction passed its live boundary: manifest cursors were read successfully as `observation`. Homepage snapshots were consumed and the requested search submitted. The search snapshot contained 52 pages with no capture truncation. Only the first search page was read; no product visits or workbook creation followed. Execution stopped with `Journal persistence failed`. The unchanged oracle reports `workflow_execution_failed` and `workbook_missing`.

The retained checkpoint has 3,758 invocations, 11,853 events and a 1,329,124-byte authoritative record. A `set` with 16,268 bytes of resolved input retained **745,539,907 logical bytes of prior state**. Its second record-copy loop result contains the first loop's results in each iteration. Logical lengths were calculated over verified immutable blocks without reconstructing the large journal. A process sample measured approximately 12.5 GB footprint, 15.1 GB peak. The exact initial persistence exception is masked by the subsequent stop exception; memory amplification is established, but the specific storage failure cause is not.

The [small reproduction](evidence/cursor-live-2026-10-06/loop-amplification.py) uses two ordinary loops and no journal, MCP or model. With an 8,192-byte source, second-loop result sizes are 166,065 / 597,985 / 2,264,231 bytes for 4 / 8 / 16 items. Business values and order remain exact. This demonstrates amplification before storage deduplication.

```sh
python3 docs/evidence/cursor-live-2026-10-06/loop-amplification.py --root /path/to/frozen-checkout
```

All 16 workflow MCP calls and two runtime model calls have observed durable completions. No workflow cleanup receipt is present; the oracle observed no active Browser page and issued its own close. Those oracle actions are not workflow cleanup evidence. There was no reconciliation or replay.

| Stage | Logical calls / physical attempts | Input / output tokens | EUR | Latency |
| --- | --- | --- | --- | --- |
| Planning, including two review revisions | 6 / 8 | 73,087 / 31,791 | 1.177405 | 1,263,274 ms |
| Execution | 2 / 2 | 51,392 / 388 | 0.239736 | 816,474 ms |

Planning consumed two automatic repairs and two discovery reads. Runtime mapping calls were zero: the artifact contains typed copy loops and explicit interpretations. The two reserved runtime requests were 41,281 and 133,583 UTF-8 bytes; these are stored Flow request sizes, not raw provider-wire sizes. All new usage is verified. Campaign upper bound is **EUR 88.87480257548656165475810439 / 150**, including unchanged **EUR 2.6067527839643652561247216036** unknown reservations.

[Review and unchanged artifact](evidence/cursor-live-2026-10-06/amazon-review.md) · [Oracle and failure](evidence/cursor-live-2026-10-06/execution-result.json) · [Journal measurements](evidence/cursor-live-2026-10-06/journal-measurement.json) · [Exact accounting and six-slot report](evidence/cursor-live-2026-10-06/cohort-final.json).

The frozen candidate's CI completed with 29 successful and four skipped checks. Its prior full solution result remains 4,590 passing tests with 12 existing skips; this evidence-only commit changes no production code. The live result is **0/1 attempted execution oracles passed**, with five unexecuted cohort slots. PR #117 remains draft. No code-review evaluation or cohort expansion occurred.
