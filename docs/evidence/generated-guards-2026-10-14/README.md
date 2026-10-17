# Generated guards and deterministic composition

Source: PR #117, `c2067c59`. Production changes are limited to fresh planning
instructions and a fresh full-proposal schema annotation. Compiler v15, runtime,
repair authority, limits, public contracts and historical requests are unchanged.

## Diagnosis and exact retained replay

Session `f4757fdbadc94a29ae1b63c3fe2ca6f9` stopped at revision 4 after two calls and
zero repairs. The second committed response already contains the two literal
`available},{` property names. Its text and parsed JSON agree; both receipts still
validate against their original issued response schemas. The malformed references
are generated content, not parser/storage corruption. They cause two
`TASK_FIELD_UNKNOWN` diagnostics and two corresponding `REVISION_REQUIRED` notices.
Automatic repair correctly cannot edit these assertions.

[replay.json](replay.json) records original hashes, schema checks, detached revision
state and unchanged-source checks. [The replay program](replay/Program.cs) uses
public encrypted-storage APIs, checks tenant ownership and pending state, and
rejects every MCP invocation. It modifies only a detached session. Three
deterministic responses exercise existing cleanup discovery, exact-contract
inspection and structural revision; they are not provider calls. The copy keeps
the original cumulative allowance and prior two calls, reaches review with zero
repairs, and remains unapproved. The retained session, both receipts, reservations
and financial state are unchanged.

- [Original TaskPlan](retained-taskplan.json).
- [Minimal two-reference correction](reference-only-taskplan.json): only the two
  malformed ports become `available`; both original assertions remain. It compiles
  but does not fix the unsuitable composition.
- [Corrected proposal and requirements](corrected-proposal.json),
  [TaskPlan](corrected-taskplan.json), and [actual v15 YAML](corrected.yaml).
- [Sanitized reproduction and compiler outputs](generic/), paired with source
  hashes in the test fixture's `identities.json`.

No field sanitizer was added. A declared field containing punctuation or Unicode
remains valid. The isolated targeted-revision test proves exact two-path authority;
the larger correction deliberately uses the existing requirement-changing
structural revision, without `EditablePaths`.

## Fresh guidance

Replace overlapping shorthand with an executable nested binary predicate:

```text
success AND (available AND complete)
```

The actual emitted JSON example uses three declared output bindings, never an
asserted constant. The compact task annotation states that `and`/`or` are binary,
`requires: false` fails, and `field.port` is one exact property name. The example
and skill explain deterministic producer flags and short-circuit protection of
nullable access. Existing exact-description guidance remains intact. Private
repair templates, issued requests and authority fingerprints are untouched.

The tight retained discovery fixture still exposes all 74 discovered capabilities
and ten required contracts in seven deterministic calls, zero repairs. Complete
request estimates are 9,535; 21,222; 21,593; 21,411; 21,330; 21,544; 23,955, below
its unchanged 24,000 ceiling. The initial verbose draft exceeded that gate; the
failed attempts are retained. Duplicated prose was shortened, not contracts,
coverage, admission margins or budgets.

## Exact composition differences

| Area | Original | Corrected offline proposal |
| --- | --- | --- |
| Requirements | Four accepted outcomes | Same four, unchanged; Browser cleanup is the sole addition |
| Public interface | `article: string` → `filePath: string` | Unchanged |
| Search | Bounded query-to-URL interpretation, then Browser acquisition | Same conversion responsibility and operation literals |
| Acquisition status | Two interpretations of full producer results; missing completeness checks | Ordered deterministic success/`ok`, snapshot presence and capture/manifest completeness assertions; each page checked before extraction |
| Candidate extraction | One object per page despite the requested array | One compact array per complete page; `Candidate[][]`, explicit one-level flatten; page bound remains separate from the ten selected products |
| Selection | Interpretation reconstructs original records | Compact decisions return unchanged IDs; offered membership precedes strict original-record lookup |
| Visits | URLs supplied by reconstructed records | Original observed URLs; missing required URL fails before that visit |
| Product facts | Interpretation returns row values | Compact ID decisions plus nullable branches and deterministic original-text lookup; complete description block copied verbatim |
| Writing | Interpretation assembles rows; separate empty/nonempty writers | Typed cells and `flatten([[headerRow], productRows])`; one writer preserves both branches' identical literal arguments |
| Result | Writer path | Actual writer success asserted, then writer-produced path |
| Cleanup | Absent | Existing Browser finalizer; uncertainty protections unchanged |

The headers remain `Nom`, `Description`, `Prix`. The exact retained destination is
`workflows/extraction-amazon-articles/articles_amazon.xlsx`. Navigation format,
wait condition, timeout, maximum records, writer encoding, append flag and other
literal arguments are preserved. Search precedes individual visits, which precede
writing and cleanup. The generic local fixture uses an equivalent confined test
destination and loopback URLs. No unrelated action, filtering or deduplication is
added. Repeated selections remain repeated visits and rows.

An empty, complete local result produces exactly the supplied header. A local
assertion additionally rejects empty selections when candidates were offered,
after strict membership validation and before visits. It compares existing `json`
bindings against the exact empty-array representation; only bounded compact
arrays enter this check, never full observations. Failed or incomplete acquisition
cannot take this path. This fixture does not establish that a future mapping
correctly recognizes all candidates on an unfamiliar site; independent business
review remains necessary.

## Size and inference measurements

These are composition changes, not a compiler optimization comparison. The
minimal reference correction still omits essential business checks and source
reconnection; its smaller YAML is not an equivalent implementation.

| Sanitized composition | Tasks | Static steps | Sets | Workflows | YAML bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Reference-only correction | 14 | 23 | 1 | 5 | 42,966 |
| Initial complete correction | 31 | 45 | 12 | 15 | 203,980 |
| Direct-row correction before empty-selection assertion | 27 | 37 | 10 | 11 | 197,658 |
| Final correction with empty-selection assertion | 27 | 43 | 14 | 11 | 202,007 |

The final exact retained-contract artifact is 202,113 bytes; server names,
capability identities and preserved destination account for its difference from
the sanitized comparison. The initial complete variant borrowed independent
row-contract groups from a fixture exporting rows publicly. This interface only
exports `filePath`; typed literal cells, the bounded projection and the writer's
schema suffice. Removing those unused groups preserves all applicable checks.

The final empty-selection assertion adds v15 guard materializations. This
correction preserves those checks rather than changing the compiler to improve a
count. Remaining technical tasks retain original page records and completeness checks,
resolve nullable IDs with ordered offered/original lookups, capture exact values
across nested item scopes, enforce the ten-row projection bound, or check writer
success before cleanup. They use existing bindings and v15 lowering. No inference
remains for status, copying, ID reconnection or row/header assembly. The remaining
interpretations are bounded query conversion, product selection and per-product
fact-ID selection; extraction maps complete page collections.

Local execution measurements, including cumulative allocation (not peak memory),
logical/physical journal sizes, invocations and restart costs are in
[local-measurements.json](local-measurements.json). They include actual local
components and deterministic adapters; they are not estimates of paid provider
cost or production latency.

## Independent local execution evidence

The eleven variants exercise complete, one, empty, empty selection despite offered
candidates, missing, repeated, null URL, unknown ID, unoffered ID, incomplete
acquisition and denied writing. Real Flow,
Browser and Document components use local networking and deterministic inference.
Planning reaches clean review before disposable test approval. No retained
artifact is approved or executed.

Successful cases inspect actual visits, original navigation arguments, all complete
pages, candidates outside mapping examples, exact descriptions, headers, cell
order, blank positions, output location and Browser closure. Restart uses the
committed result with zero repeated inference or external calls and identical file
bytes. Failure cases publish no workbook; null URL stops at the affected third
visit, and incomplete acquisition stops before extraction. The existing strict
lookup rejects unknown/unoffered identities rather than inventing records.

[Independent ZIP/XML inspection](inspect_local_workbooks.py) compares saved local
producer observations against the [five actual workbooks](local/) and produces
[workbook-inspection.json](workbook-inspection.json). This is separate from the
workflow success flag and model response. The unchanged live oracle accepts the
complete/one/repeated cases and rejects a deliberately rewritten description.
It still conservatively rejects header-only business completion and unsupported
absence claims. Controlled local-site ground truth establishes the missing-field
and empty-result fixture expectations; no historical verdict was changed.

## Validation boundary

Focused evidence includes actual emitted generation/revision requests, two-path
authority, stale revisions, unrelated-edit rejection, literal punctuation,
short-circuit assertions, existing discovery budgets, deterministic structural
revision and local workbook/recovery checks. Failed development attempts remain
under [failed-attempts](failed-attempts/).

The final focused host gate passed 18 checks: eleven executions, six unrelated-edit
rejections and the retained/structural-revision comparison. The guidance gate
passed twelve checks, including the tight discovery budget. Independent ZIP/XML
inspection passed for all five produced workbooks. Release Core/Planning/Persistence
packages, Native AOT planning smoke, published-host encrypted recovery and skill
validation passed; [publish evidence](publish-checks/results.json) identifies its
build. The final serial full-solution and candidate CI completion are recorded in
the PR #117 delivery report, separately from these owning gates. An earlier full
run was intentionally interrupted to add the empty-selection regression, retained
under failed-attempts, and is not counted as a pass. This work does not establish
future model compliance or real Amazon business acceptance. Applying a paid
revision to the retained session, approving its new artifact and executing it are
separate actions requiring authorization. PR #117 remains draft.

The full run subsequently exposed obsolete prompt-text assertions in the older
v10 and initial-composition host fixtures. Their expectations were updated to the
replacement guidance; compilation, call-count, request-size, workbook, effect and
recovery assertions are unchanged. The two v10 review cases and the real
initial-composition execution then passed (ten visits, 29 complete pages, exact
workbook cells, zero typed-copy inference). The failed full-run evidence and
[three-case verification](host-guidance-final.log.gz) are retained. A fresh complete
serial run, rather than that isolated rerun alone, is required for final acceptance.
