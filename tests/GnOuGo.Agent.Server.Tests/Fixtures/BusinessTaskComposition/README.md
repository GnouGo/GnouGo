# Smaller business composition

`baseline.json` sanitizes the 23-task proposal retained in
`genericbrowser20261009a-amazon-1`: neutral task/operation names and objectives,
the same public output contracts, and a disposable workbook destination. No
saved proposal, approval or execution is changed.

`compact.json` expresses the same business work in 13 tasks using existing v5
compilation. Completeness predicates are local `requires`; candidate extraction
returns an array per complete page; flatten and lookup are consumer bindings;
typed rows and writer results are exported directly. Missing fields remain null
inside non-null records. There is no added filtering, deduplication or ranking.
Extraction omits per-page identity/error wrappers that its consumer does not
need. The producer's actual error metadata is already a direct reconciliation
input; an extraction script must not manufacture a null error field. The retained
baseline is a compilation comparison, not evidence of successful historical
execution (the paid run stopped before extraction).

The two necessary technical tasks are existing pure projections:

- `originals` exports original record arrays without learned copying.
- `verified_ids` consumes offered-candidate lookup before exporting IDs to the
  original-data lookup. It prevents selecting an original record that was never
  offered, and preserves repeated selections.

Neither projection has per-item effects, inference, guards or cleanup, and both
use existing deterministic lowering. The effectful `visit` loop remains a real
iteration. URL construction and bounded TSV formatting retain their explicit
transforms because the current binding vocabulary cannot express those string
conversions. Extraction and reconciliation remain separate semantic operations;
no new runtime feature hides inference.

Tests compare compilation metrics, exercise actual Browser/Document transport,
and inspect visited pages and workbook cells independently. Deterministic
adapters establish execution regression coverage, not live-provider acceptance.
