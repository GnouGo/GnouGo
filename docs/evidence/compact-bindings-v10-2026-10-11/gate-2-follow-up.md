# Gate 2 follow-up found by Native AOT

Candidate d9c73073 published but its Native AOT smoke failed the existing
nullable_defaults fixture. v9 validated the branch's independently checked capture
export; v10's eager copy inlining exposed the upstream nullable declaration again,
so static validation rejected the non-null branch output. No external work occurred.
The first full solution run was interrupted to fix this known deterministic failure;
it is not counted as a passing gate.

The smallest correction removes eager capture-wrapper elimination. Pure projection
envelope removal, descriptive locals and syntax-aware formatting remain. Both v9/v10
now validate and execute the null and non-null variants with the original contracts.
76 focused equivalence/benchmark checks and two local composition/execution checks
pass. An initial test reused its effect recorder across two runs and correctly
failed the exact-operation-count oracle; the corrected comparison isolates each
execution's observations. Assertions and limits were unchanged.

Final same-composition YAML: 195,783 -> 189,337 UTF-8 bytes (65 steps, 38 sets).
This supersedes the preliminary 189,133-byte result. The fresh local plan reaches
review in one deterministic call, zero repairs (22,354 estimated request tokens),
then verifies ten visits, 29 pages, all 12 candidates, exact XLSX cells and cleanup.
329 actual invocations and approximately 31.14 MB logical journal; the complete
selection request remains 2,597 bytes. No inference for typed copying or rows/status.
Full solution, Native AOT/recovery and CI must rerun on the corrected candidate.
