# TaskPlan stabilization: deterministic fixes, evaluation pending

This pass continues from `80e50fa`. Prior evidence remains unchanged; its hashes are retained alongside this report. Four sanitized synthetic response sequences reproduce the unsuccessful TaskPlan runs without copying provider prompts, raw receipts, credentials or host settings.

The initial regressions produced six failures and one pass. The passing test established that correcting the exact cleanup producer ID was already supported; wholesale rewrites must remain rejected. Contract and compiler fixes reduced failures to three, then scoped repair changes passed all seven initial regressions. Additional output timing, nested binding, discovery and recovery coverage brings the planner suite to 153 passing tests.

Changes are confined to the existing response schema, compiler/source maps and scoped repair. Public contracts and storage versions are unchanged. Independent input errors are reported together. Exhausted discovery cannot advertise another source action. Structured scope outputs use typed `set` stages after cleanup. Group inputs and nested outputs receive stable semantic repair locations, and unauthorized edits are identified without replacing the accepted baseline.

`freeze-contract.json` records the implementation revision, unchanged model/limits, campaign ceiling, oracle hashes and acceptance rule before evaluation. Exactly three complex cases with three repetitions are authorized. The prior campaign upper bound is EUR 42.2723101900143 of EUR 50; admission remains authoritative and all uncertain reservations remain charged.

No new paid evaluation has started. Full validation and CI are pending. PR #113 stays draft, and real sandboxed Copilot edit/test execution remains unverified.
