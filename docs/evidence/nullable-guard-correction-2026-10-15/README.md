# Nullable guard correction and disposable composition

Baseline: `1117f105`. Production correction: `6566db6`. This work is offline:
no provider request, live execution, campaign update, historical revision,
approval, archival or financial change was performed. PR #117 remains draft.

## Correction proof

The original proposal uses a nullable string and a nullable snapshot object as
`requires`. Both are invalid Boolean conditions. The recorded targeted patch
retains each reference in `value != null`, conjoining a declared Boolean from
the same producer. Version-1 preservation rejected this valid type correction
because the guard's syntax changed. The compiler itself was correct.

Fresh private correction bookkeeping uses version 2. Only a real
`TASK_CONDITION_TYPE` at the retained guard can authorize this conversion.
Reference schemas come from the existing compiler binder. Valid surrounding
predicates stay in place; nullable operands in an already valid comparison are
not eligible. Unknown types, invented success, completion substitution, new OR,
removed/reordered assertions and unrelated producers remain blocked. Unsupported
guard edits stop without an unhelpful structural regeneration. Existing cumulative
limits and the single fallback for other supported failures are unchanged.

The actual targeted patch prompt now contains the narrow guidance. Previously,
the repair renderer removed structural revision context. Issued requests and
private repair schemas are not rewritten. Version-1 progress retains the old
preservation rules, including on restart. Independent task changes identify the
exact changed field; transform objectives remain protected behavior.

[Retained replay](retained-replay.json) records original request/receipt/schema
hashes. Its [read-only executable probe](retained-replay.cs) uses public encrypted
KeyVault APIs, verifies ownership and stopped revision 5, validates all four
responses against their original schemas, and supplies saved payloads to a
memory-only runtime. The same patch is rejected under private version 1 and
reaches review under version 2: two deterministic responses, one correction,
zero approval, zero provider/MCP calls. No subsequent compiler diagnostics remain.
This does **not** certify the retained proposal's business composition.

A separate replay deliberately returns an empty targeted patch, then the retained
regeneration. It rejects the changed independent objectives at their exact
`/tasks/.../objective` locations, preserves the contract diagnostics and stops
without another request. No fields in the rejected response are silently restored.

The initial 11 regression cases failed before the change. The expanded focused
correction/revision suite passed 105 checks. Four additional parameterized
runtime cases pass for nullable strings, objects, arrays and numbers: present
values (including `""`, `{}`, `[]` and `0`) reach the dependent operation, while
null, absence and a false producer flag prevent it. All 25 guard regressions pass. Two normal Agent.Server background
tests prove review or a bounded stop, exact cumulative accounting, no save or
approval, tenant isolation and no further calls after polling/restart.

## Separate composition proof

The [original proposal](../initial-requirements-live-2026-10-15/initial-proposal.json.gz),
[exact saved guard correction](minimal-guard-taskplan.json), and
[corrected disposable proposal](composition/corrected-proposal.json) are separate.
The latter uses one explicit structural revision with deterministic responses;
there is no automatic structural-correction claim.

All six requirements and public input/output contracts remain unchanged after
ordinary DTO default expansion. The [operation/interface diff](composition/differences.json)
records removal of unrequested consent work. Search, individual visits, writer
and cleanup retain their literal arguments and relative order; only URL data
bindings and row assembly change. The exact destination is retained but local
execution writes beneath a disposable workspace, never the historical workspace.

Complete pages feed independent extraction into candidate arrays. Compact ID
decisions reconnect offered candidates before original records. Names, prices
and complete descriptions are copied from originals; missing facts remain null.
The writer receives `flatten([[headerRow], productRows])` with `Nom`,
`Description`, `Prix`. The successful writer supplies the public success/path;
its checked success boundary permits deterministic `completed` and empty errors.
Failed acquisition or writing produces a failure, not a successful empty record.

| Static composition | Guard-only retained | Corrected |
| --- | ---: | ---: |
| TaskPlan tasks | 17 | 27 |
| Compiled steps | 37 | 43 |
| Sets | 8 | 14 |
| Actual v15 YAML bytes | 100,647 | 203,375 |
| Interpretation stages | 6 | 3 |
| Extraction stages | 2 | 2 |

This is a correctness correction, not a lowering optimization. The extra typed
projections retain original page records, check bounded products, resolve three
nullable selected facts and capture them across nested item scopes. Existing
bindings cannot project those collections or scalarize a strict singleton lookup
without these scopes. Offered lookup, original lookup, nullable branches and
writer validation remain independent checks. Compiler v15 is unchanged.
See the complete [guard-only YAML](composition/guard-only.yaml) and
[corrected YAML](composition/corrected.yaml).

## Local execution and independent workbook checks

All 12 targeted host/composition/execution checks passed with `-warnaserror`.
Nine real local Browser/Document cases cover complete/empty acquisition, missing
facts, repeated selection, unknown/unoffered IDs, incomplete acquisition, missing
required navigation URLs and denied writing. Each uses deterministic inference
and loopback networking. Unsafe cases prevent affected work; cleanup remains
effective on known failures. Existing uncertainty/recovery suites cover the
separate prohibition on unsafe cleanup/replay.

The complete and repeated cases each visit ten products, validate all 39 source
pages and produce 11 Excel rows. Runtime uses 23 deterministic inference requests:
one bounded URL conversion, one search extraction, one product selection, ten
product extractions and ten fact selections. Copying, lookup, status and rows use
zero inference. Restart reuses completion with zero additional inference/effects
and identical workbook bytes.

[Independent XML inspection](workbooks/independent-inspection.json), reproducible
with `python3 inspect-workbooks.py workbooks`, compares actual workbook cells to
original producer records, navigation arguments and close receipts. Four XLSX
files pass: complete, missing fields, repeated products and a genuinely empty
complete acquisition. No formulas are created from formula-looking text.
The unchanged general live oracle passes complete/repeated cases and remains
conservative for missing/empty cases; fixture ground truth independently proves
those blanks/header-only rows. Historical oracle verdicts are unchanged.

The [runtime measurements](metrics/) separate allocation, logical journal size,
physical storage, checkpoints and recovery. For the complete fixture: 288 actual
invocations, approximately 5.55 GB cumulative managed allocation, 46.38 MB logical
journal, 23.18 MB physical fixture storage and 12.7 seconds measured execution.
Allocation is not peak memory or sandbox usage. No resource ceiling changed;
these measurements make no performance-improvement claim.

## Final validation and remaining limits

Full serial solution, Release packages, Native AOT, published encrypted recovery,
skill validation and candidate CI results are recorded in the final validation
report and PR update. Focused successes do not substitute for those gates.

The historical `initialrequirements20261015a-amazon-1` stays stopped at revision 5.
Its four receipts, `[unknown, 200]` transport history and reservation remain intact.
These proofs establish safe correction of this diagnosed defect and exact local
execution. They do not establish future model compliance, recognition of every
unrequested action or real live business acceptance.
