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
