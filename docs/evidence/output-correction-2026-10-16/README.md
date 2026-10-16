# Diagnosed output correction and executable business composition

## Gate A — Correction preservation

Baseline: `9a773f53`. Retained Designer session
`51b24cad299443eb8f3d0391db2f8285` remains stopped at revision 19.
No historical session, receipt, approval, reservation or execution was changed.

All eight retained responses satisfy their original strict provider schemas. The
accepted interface nevertheless excludes `generation_summary`, `status` and
`workflow_interfaces`. Response 8 only removed its diagnosed extra export, but
preservation compared against the invalid proposal instead of permitting that
specific removal. Response 6 additionally changed an operation's instruction
argument and introduced an optional, nonnullable object output as explicit null.
Those are separate defects; permitting extra-export removal must not authorize
either change.

Fresh private correction version 3 records the diagnosed unaccepted export names
before issuing a correction. It permits only their removal, preserves other names,
order and independent bindings, and retains full validation. Version 1/2 requests
and committed receipts retain their original handling. Existing nullable-guard
correction remains available. No slot, compiler profile, public API, runtime or
allowance changed.

The initial executable regression failed on extra-export removal. After the fix,
107 focused checks passed with `-warnaserror`; four Agent.Server background-host
checks passed, including bounded failure, polling, restart and tenant isolation.
Negative cases reject changed arguments, requirements, surviving bindings,
reordering, renaming and replacement exports. Optional omission remains distinct
from explicit null. The receipt-recovery regression verifies exact request reuse,
cumulative accounting, stale-revision rejection and no approval.

[`replay.cs`](replay.cs) reads public encrypted-storage APIs and evaluates only
disposable session copies. [`replay-report.json`](replay-report.json) records
original request/receipt hashes and every diagnostic. Historical versions still
reject both retained corrections. Under fresh version 3, response 8 reaches review
with one deterministic response and no approval; response 6 remains rejected.
Removing only the unauthorized exports from response 5 also reaches review.
All requests fit the existing 24,000 request ceiling. Original records are checked
again afterward. Provider calls and external operations: **zero**.

Static acceptance here does **not** prove business completion. The retained plan
asks a runtime agent to write another workflow file; it does not itself carry out
the requested code review. That composition requires a separately reviewed
proposal, rather than silently changing accepted requirements or operation
instructions during automatic correction.

## Gate B — Requested execution versus artifact generation

The fresh composition instruction now says to execute requested work and to create
code/another workflow only when that artifact is itself requested. The existing
verbatim-description rule is qualified accordingly; its exact-block selection,
null and strict-lookup rules remain intact. This replaces existing prose, adds no
semantic checker and does not prohibit handwritten or legitimate artifact-writing
workflows. Tests inspect actual generation and revision requests and retain request
size and discovery coverage.

The retained proposal cannot acquire new operations or change accepted outcomes
through the export-removal fix. The separate generic fixture exercises the existing
explicit, requirement-changing `revise` command, with revision and artifact hash,
on an in-memory disposable session. No automatic approval or execution permission
is produced. The fixture directly executes only local test adapters afterward.

| Responsibility | Artifact-only fixture | Explicit business revision |
| --- | --- | --- |
| Caller inputs | Change URL and instructions | Same names and contracts |
| Accepted output | `workflow_file` | `decision`, `summary` from submission |
| Accepted outcomes | Write another workflow | Review/checks, original feedback, cleanup |
| Runtime operations | Write a blueprint | Inspect → clone → checks → review → begin review → comments → submit → cleanup |
| Conditional work | None | Inline/global comments; approve/request changes; cleanup only after acquisition |
| Typed transport | Artifact path | Original findings, repeated occurrences and submission outputs; zero inference for copying |

These output/outcome changes are **explicit disposable revision changes**, not
authority granted to automatic correction. The retained session is not migrated.
The sanitized [artifact-only proposal](artifact-only-plan.json),
[corrected proposal](corrected-proposal.json), and actual compiler-generated
[v15 YAML](corrected-v15.yaml) are reviewable. The original session uses different
provider contracts; its host's recorded coding capability exposed project read/write,
not enabled command execution. The fixture does not establish that the operator's
live host is ready to perform checks or publish a review.

Seven scenarios pass: approval, exact/repeated findings, failed checks requiring
changes, incomplete review, denied clone, confirmed comment failure, and unknown
comment completion. They use real Flow, real Git MCP cloning of a local repository,
and four actual local check processes. Copilot review and GitHub actions are
deterministic in-memory adapters with validated fixture contracts; no network
review or provider call occurs. Original files and an unrelated sentinel remain
unchanged. The oracle verifies comment text/IDs/order, one clone, decision and
summary, and cleanup. Incomplete review cannot begin publication. Unknown comment
completion preserves the clone and blocks replay and cleanup. Reopening the run
with no inference/tool adapters returns its recorded outcome without further effects.

[Measurements](composition-metrics.json): two deterministic planning responses
(initial proposal plus explicit revision), zero repairs, zero runtime LLM calls,
one scripted review operation, requests estimated at 9,714 and 10,151 within the
unchanged allowance, and 20,537 YAML bytes. A successful approval path has seven
fixture operations; the findings path adds three ordered comment occurrences.
This is a composition correction, not a step-count optimization: no compiler or
runtime lowering changed.

The passing predetermined proposal proves that the composition is expressible and
executes correctly against these contracts. It does not prove future model
compliance, arbitrary repository check coverage or the quality of a real code review.
