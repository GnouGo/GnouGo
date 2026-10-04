# Bounded semantic repair patches

Historical design and measurements from before planner stabilization are retained below.
Current version-2 structural permissions and execution comparisons are documented in
[planner stabilization](evidence/planner-stabilization/README.md); older pending
requests continue to use their original narrower schemas.

Initial generation still returns TaskPlan. A semantic repair returns only private,
source-generated `RepairPatch` data. The host issues typed slots from the immutable
baseline, existing `RevisionScope`, diagnostics and authoritative contracts. Slot IDs
are not JSON pointers. Explicit replacement, addition and removal are distinct;
null remains a business value. Catalog-owned bindings permit only diagnosed removal,
including explicit removal of owned descendants while retaining editable members.

Patches cannot insert, delete, rename or reorder tasks. Existing permissions still
control local binding/type/dependency changes and explicit export chains. The host
applies edits to a clone, runs the same revision validator, then fully compiles and
validates the candidate. Unknown, duplicate, conflicting, stale or unauthorized edits
never replace the baseline. Empty/unchanged patches stop without progress.

Only affected fragments, immutable objectives, necessary interfaces, diagnostics and
producer/consumer contracts enter repair prompts. Read-only producers retain complete
output contracts without unrelated input arguments. Available-port names and scope
ordering remain visible for choosing explicit references; adding unrelated task bodies
does not copy their contents into the repair context. Full plans and receipts stay in
host state. The original request fingerprints baseline, scope, contracts, tenant,
policy and ceilings. Recovery uses its original schema and identity, never a rebase.
Historical full-plan responses remain supported only for requests issued with that
schema. Operation-changing repairs retain the existing bounded discovery action;
fixed-operation repairs cannot discover. No new phase, public DTO or storage format.

## Retained failure and deterministic comparison

Base: `0346a63`. Session `2e5d817e315a422b80f5349e6bec6bfb` used seven calls and two
rejected full-plan repairs for six editable slots. All seven responses, issued
schemas, exact contracts and accounting are preserved in the sanitized
[recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/RepairPatch/retained-repair.json).
Original encrypted records and saved counters were not changed. Historical replay
retains both unauthorized-rewrite failures, including pending-request recovery.

A separately identified synthetic response explicitly removes the optional
`startLine` binding and sets only the diagnosed `baseRef` and `headSha` producer
nullability leaves to false. Objectives and all other fields remain identical.
Each recorded state reaches final review using one scripted response, no metadata
reads, full executable validation and approval recompilation. Runtime regressions
also show that explicit narrowing does not invent missing data: null/incorrect
values fail before mocked dispatch.

| Measurement | First before → after | Second before → after |
| --- | ---: | ---: |
| Prompt UTF-8 bytes | 56,369 → 28,887 | 57,212 → 29,730 |
| Response schema bytes | 22,032 → 5,173 | 22,032 → 5,173 |
| Complete estimated input tokens | 26,390 → 11,610 | 26,671 → 11,891 |
| Minified response bytes | 10,050 → 172 | 10,932 → 172 |
| Scripted repair calls | 1 → 1 | 1 → 1 |

Input reductions are **56.0% / 55.4%**; responses are **98.3% / 98.4% smaller**.
Both fit the unchanged 24,000 input allowance. The comparison uses identical
sanitized encodings; redaction removes 21 prompt bytes versus the original private
recordings. Response figures include the complete response envelope, not just its
old TaskPlan. Machine-readable measurements and fixture hash are in
[the evidence record](evidence/flow-v9-112/bounded-semantic-repairs.json).

## Validation and limitations

Focused regressions cover atomic rejection, required/optional/owned removal,
conditional exports, nested/group constraints, dependency edits, preserved choices,
unknown/duplicate slots, unrelated changes, stale authority, pending recovery and
approval recompilation. Existing scripted tests now emit patches for new requests;
historical recordings and their original schemas are unchanged. Native AOT smoke
covers private source-generated patch serialization and retained-request recovery,
alongside all eight deterministic business scenarios. All five Flow packages build
independently with warnings treated as errors.

The first measurement failed the 50% input target because it retained read-only
producers' input contracts; removing only those irrelevant arguments meets the target
without trimming their output contracts. Initial test-transport failures and the
size failure remain in local validation logs. Full solution validation also reproduced
an existing concurrent telemetry-eviction failure. A separate focused regression
failed before the fix; sorting an atomic dictionary snapshot fixes it without
changing planner/runtime contracts or trace retention settings.

Exact final revisions and CI results are recorded in draft [PR #113](https://github.com/GnouGo/GnouGo/pull/113).
No paid inference, external workflow or real Copilot task was run. Smaller patches
cannot guarantee correct business decisions or live reasoning-token savings. Mandatory
repair context can still exceed its hard limit and must stop. The existing real
Copilot sandbox limitation remains; this work does not establish live execution
success. The earlier Windows Cmd startup fix passed native branch CI at the baseline.
