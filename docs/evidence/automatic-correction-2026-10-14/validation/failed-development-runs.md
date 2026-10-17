# Retained failed development runs

- `local.log.gz`: three new fixture cases assumed a nonexistent `field(success)` node. The fixture was corrected to mutate an actual declared output port. The following 22-case local run passed; assertions remained strict.
- `planning-all.log.gz`: 1 of 1,365 tests failed. Terminal diagnostic presentation changed an issued repair authority after interruption. The planner now preserves diagnosed codes while a request remains pending. The exact-receipt recovery regression subsequently passed at an exhausted call/replan allowance.
- `preservation.log.gz`: one new branch-movement fixture omitted its required objective, so provider-schema validation rejected it before the intended preservation check. The fixture now includes the objective; the branch-preservation assertion was not weakened.

- `ci-host-failure.log.gz` and `ci-planning-failure.log.gz`: candidate `02569f7` failed to compile the newly added retained-proposal test because of a missing namespace import for `RealProductContracts`. CI completed all 1,370 Planning tests before the host compile error. Commit `a23f725` adds that import without changing production behavior or assertions.

These runs are retained as failures, not included as successful final acceptance.

- `guard-order-before.log.gz`: two statically valid proposals incorrectly reached review after moving an existing assertion. The new ordering/scope/dependency preservation checks reject both; `guard-order-after.log.gz` passes 35 focused cases. No proposal from the failing run was approved or executed.
