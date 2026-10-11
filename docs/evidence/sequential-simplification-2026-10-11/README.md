# Sequential simplification — offline gates

Baseline: `81b6b0e9106e3debf25580b34f49634436d0d03f`, PR #117, draft.
No paid inference or historical workflow replay is authorized by this correction.
The retained live completed ten visits and writing but failed independent acceptance;
its result, receipts, approvals and financial reservations remain unchanged.

## Gate 1 — readable checked JavaScript

Fresh sessions/revisions select `compact-bindings-v9`. Historical profiles retain
exact lowering. The existing final-lowering AST now removes redundant parentheses,
uses native literal/shorthand syntax and aliases exact copies of checked locals.
Both declarations and both ordered contracts survive aliasing. Existing producer
projection boundaries and first-present helpers remain necessary and are retained.
No sandbox, permission or resource allowance changed.

Measurements on the identical business fixture: v8 99,671 YAML bytes; v9 99,319.
On the ordered-check fixture: 2,648 → 2,638 bytes, unchanged execution/failure boundaries.
Scalar selection succeeds beside eight 1.25 MB observations without importing them.
These are compiler-only comparisons; task-count reductions are measured separately.

Validation: 1,253 Planning tests (including 59 focused compilation/execution checks),
27 expression-contract checks and eight real local Browser/Document scenarios passed
with `-warnaserror`. Local scenarios include exact workbook inspection, repeated
selections, empty collections, incomplete observations, denied writes and fabricated
content rejection.
An intermediate formatter attempt removed required producer-address parentheses;
the local semantic validation rejected it. The final correction preserves that
single boundary without relaxing semantic validation.

## Gate 2 — business composition

The actual compact-schema prompt now states that typed copying needs no inference
and interpretation receives closed consumer fields without raw originals. The
prompt was compressed after a discovery regression caught a 44-token overrun;
the original 24,000-token bound and discovery assertions are unchanged.

A sanitized counterpart of the last proposal removes three forwarders: tasks
17 → 14; YAML characters 96,243 → 93,720. Compiled steps/sets remain 38/18 because
v9 already fused these forwarders. The offered-ID validation and writer assertion
remain independent. No checks were removed to manufacture a step-count reduction.
The older same-work comparison remains tasks 23 → 13, steps 61 → 60, sets 39 → 36,
YAML bytes 92,585 → 77,588. Existing typed projection uses no inference.

Validation: 98 Planning discovery/input-budget/revision tests and seven host
composition/contract/discovery checks passed with `-warnaserror`. The unchanged
compact execution composition passed all eight real local workbook scenarios in
Gate 1. Integrated structured-row execution is validated in Gates 3 and 7.

## Gate 3 — exact cells and observed facts

`document_write` now offers optional XLSX string/null rows, exclusive with legacy
text. Existing C# calls and text semantics remain available. Structured writes
validate input before effects and publish a checked temporary workbook atomically.
Worksheet XML entity-encodes carriage returns: an initial implementation normalized
CRLF; independent cell assertions caught it and the final serializer preserves it.
Formula-looking and numeric-looking values remain literal strings; null cells keep
their positions. Actual discovery publishes the exclusive input contract.

Mapping instructions/skill distinguish verbatim observations from compact decision
fields and interface labels from actual business failures. No scalar provenance
rule or sandbox limit changed.

Validation: 72 Document tests (including actual stdio discovery/write, append,
OpenXML validation and exact whitespace/null cells), 151 mapping checks, and the
real local `exact_rows` Flow/Browser/Document execution passed with `-warnaserror`.
The latter reached review in one deterministic call, no repairs; visited ten pages,
consumed 38 complete observation pages, offered all 12 candidates and matched every
workbook cell independently. Cleanup completed; no formatting inference occurred.

Local comparison: inference calls 24 → 23; mappings unchanged at 11; invocations
226 → 221; normal steps 225 → 220; finalization 1 → 1. Logical journal bytes roughly
22.64 MB → 17.52 MB. The complete selection request remained 2,597 bytes. YAML grew
99,451 → 101,212 bytes because structured row contracts replace the text contract;
this overhead is reported separately from compiler simplification. These synthetic
results are not live-provider acceptance.

## Gate 4 — program classification and cache work

The three retained `m.optional` constructed-container failures now fail preflight
with `mapping_failure_kind: program`, before item execution, without inference.
Runtime provenance also catches indirectly constructed containers. A late defect
discards earlier apparent successes before global repair; genuinely absent/scalar
observations retain data-specific specialization. Every original item and the
assembled output still validate before publication.

Content hashes are computed once per item per invocation rather than recomputed
during cache matching and writes. Existing cache-read deduplication is retained;
invalid repeated assignments evict each key once (30-item regression). Cache
identity, conservative text matching, pinned assignments and receipts are unchanged.

Validation: 157 mapping tests passed with `-warnaserror`, including cold/warm cache,
heterogeneous specialization, late atomic failure, cancellation, cumulative limits
and committed/unknown receipt recovery. Constructed-helper fixtures use one initial
call and one global repair, zero shape specializations. Retained script replay uses
zero provider and MCP calls. These are deterministic adapter results, not live claims.

## Gate 5 — bounded accounting and persistence work

Sandbox accounting now traverses JSON with bounded string-encoding scratch space,
retaining exact default escaping and numeric writers. The retained-shaped 1,265,747
byte observation charges exactly the same bytes: temporary allocation falls from
4,596,048 to 852,664 bytes. An exhaustive UTF-16 fixture checks the default writer's
escaping, Unicode, invalid surrogates, numeric spelling and null representation.

Block writing reuses its canonical hashed body when adding the owner envelope;
canonical bytes/hashes remain identical. Verified immutable scalar representations
are cached within the lease; live nodes are still compared by content on every
capture. Repeated capture allocation falls from approximately 12.57 MB to 9.75 MB
for a 9,987,879-byte logical snapshot. The 24-snapshot logical history remains
239,709,096 bytes; 150 unique blocks and approximately 1.305 MB were written.
Checkpoint writes remain 2.2–3.0 KB and approximately 6–9 ms in these local runs.
Process working-set measurements varied with the surrounding test suite and are
not evidence of an isolated peak-memory improvement.

Validation: 427 runtime/expression/mapping tests, 22 persistence tests and a separate
canonical-envelope check passed with `-warnaserror`. Coverage includes immutable
snapshot reuse, mutable executors, numeric spelling/nulls, owner/tenant isolation,
cancellation without payload reads, corrupt references, commit fault injection and
unknown-completion protection. A proposed direct clone changed a historical numeric
accessor and was reverted; that existing normalization boundary is retained.
A concurrent-build file lock and missing test cancellation arguments were corrected
by serializing dependent builds and forwarding the test token; no checks were disabled.

Gate 5 follow-up: review of the scalar cache identified a cross-kind comparison
hazard. Numeric/string/boolean kinds are now checked before cached text is used.
A dedicated mutation/restore regression passes; cached numeric spelling cannot
make a newly assigned string compare equal.
