# Controlled fixture execution budget

[Primary CI on `e25ef5f6`](https://github.com/GnouGo/GnouGo/actions/runs/42787456545)
finished with four existing Designer workbook cases failing `LLM_BUDGET_EXCEEDED`.
All cases were discovered and run, and managed binaries were unchanged. The
artifact's SHA-256 matched GitHub metadata. This failure is retained; it is separate
from the production SSE delivery investigation.

The exact error reports `elapsed_ms`, origin `explicit_workflow_limit`, allowance
120,000 ms and `dispatch_status=not_started`. The failed cases had only 32,737–40,183
ms of execution when the scope reached 120,567–125,203 ms. The fixture constructed
its execution scope before inference-free catalogue discovery, deterministic plan
revision, parsing and compilation. That preparation aged the execution allowance.
Before executing, the fixture already asserts zero planning calls and no model
requests. [Exact measurements and regressions](fixture-budget-lifecycle.json)
retain the distinction between scope age and execution duration.

The new controlled regression advances an injected clock by three minutes during
offline preparation. On the old setup, the first mapping is refused before
inference: allowance 120,000 ms, age 180,000 ms, calls zero. After the correction,
the exact workbook, visits, cleanup and ordinary fixture oracle checks complete.
Advancing the same clock by two further minutes still produces the unchanged
finite `elapsed_ms` refusal. All nine focused cases pass without warnings.

Only fixture setup changes: create one finite scope when this fresh execution
starts, asserting that no previous scope exists. The limit remains two minutes.
The host-owned execution path still captures its own existing policy. No production
budget semantics, timeout, model policy, application accounting or campaign record
changes. No used scope is replaced or reset. The original 216 fixture cases remain;
the added lifecycle regression makes the next discovered inventory 217.

The already-running, unchanged-runtime
[`ede82dc5` CI](https://github.com/GnouGo/GnouGo/actions/runs/42793088274) independently
passed all 2,064 Linux Server cases, one existing skip and all 216 original workbook
cases. A separate reader matched every result-name counter against the inventory
across all five TRX files. Its packages, Benchmark build and published smoke checks
also passed. This later pass demonstrates timing sensitivity, not erasure of the
earlier failure. The controlled red/green reproduction justifies the scoped fixture
correction without increasing a ceiling or weakening the expected result.
