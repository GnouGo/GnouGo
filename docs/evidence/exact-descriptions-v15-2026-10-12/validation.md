# Validation record

Baseline `616d5071`; description guidance `e47e6b10`; local compiler simplification `0efb485c`; bounded clarification guidance `2a95320d`; fixture assertion alignment `fa692da5`; derived identity preservation `bc321f85`. Only fresh v15 compilation changes. Retained live approvals, receipts, reservations and the unsupported-absence verdict remain unchanged.

## Owning gates

| Gate | Result |
|---|---|
| Actual generation/revision instructions and retained discovery budget | Seven targeted Planning checks passed with `-warnaserror`; no request allowance changed. |
| Complete Planning suite after guidance correction | 1,315 passed with `-warnaserror`; retained discovery reaches review in seven deterministic calls with zero repairs. Complete input estimates: 9,532 / 21,219 / 21,590 / 21,409 / 21,327 / 21,541 / 23,952 tokens. |
| Description execution | Five generic cases passed; all five independently reviewed retained blocks reconnect exactly with zero inference/MCP calls. |
| Heading-free features | Real local Browser/Document case passed, ten visits and independently checked XLSX; nullable absence case remains supported. |
| Final compiler regressions | 105 passed with `-warnaserror`, including original later-check failures and identity-sensitive counterexamples. |
| Flow ordered-check regressions | 72 passed with `-warnaserror`, including actual v14/v15 generated YAML and resource measurements. |
| Local v15 execution | Eight variants passed: complete, empty, repeated, missing, unfamiliar/redirected URL, unoffered ID, incomplete acquisition and denied write. Committed recovery repeats no external or inference call. |
| Equivalent local v14 execution | Same complete business fixture passed; all 1,089 validation origins/outcomes match in order. |
| Final business/nullable-ID matrix | 26 passed after correcting fixture assertions; actual feature-text, missing-description, repeated-selection and rejection cases preserve independent workbook/source oracles. |
| Skill | Existing `quick_validate.py` passed using the system Python with its existing PyYAML installation. |

Local tests use deterministic inference adapters and actual stdio Browser/Document components. The independent workbook inspection opens XLSX XML, rejects formulas, checks headers and all ten rows against each corresponding complete source observation, and confirms writer success and Browser closure. This is separate from the workflow's success flag.

After the final identity audit, the complete Planning suite passes **1,316** checks and the complete Flow suite **1,384**. The actual compiled branch regression fails before the correction and passes afterward; it preserves object identity reached through a derived selection. The retained v15 business YAML is byte-for-byte unchanged. All ten refreshed local Browser/Document/workbook/recovery cases pass after that correction. All five Release packs, Planning Native AOT smoke, CLI/Server publishing and published encrypted recovery pass again on `bc321f85`; see [final publish results](validation/final-publish-results.json). Final full-solution and exact-head CI outcomes are recorded in the PR delivery update.

## Full solution, packages and CI

The required full-solution command is `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`. Its local log is `/tmp/gnougo-v15-solution.log`. It started on the compiler candidate; the later guidance and fixture assertion corrections also ran in the isolated checkout. The final per-project result and exact-head CI verdict are reported in the delivery comment on [PR #117](https://github.com/GnouGo/GnouGo/pull/117). Platform skips are reported separately; they are not successes. At the time this evidence was assembled, the long full-host matrix and final CI were still running; delivery must inspect their final outcomes.

Release publishing was refreshed in an isolated checkout of `2a95320d` (the same production code as `fa692da5`), avoiding shared output-file collisions. All five Flow library packs pass with `-warnaserror`. The self-contained `osx-arm64` Planning Native AOT publish and smoke pass with `-warnaserror`. CLI/Server native publishing and encrypted recovery also pass using the repository's existing Native AOT/trim/single-file profile and pinned EF Core Tasks notice audit; no new suppression or dependency change is introduced. [Commands/results](validation/publish-results.json), [native smoke](validation/planning-smoke.log), [encrypted recovery](validation/encrypted-recovery.log), [audited notices](validation/audited-publish-notices.txt).

Owning logs and exact request-size measurements are in [validation](validation/). Final solution counts and candidate-CI links are recorded in the PR delivery update. Pending, skipped or cancelled checks are never treated as passing.

## Preserved timing observation

The supplied baseline records one five-second execution-allowance failure in `LexicalChecksDoNotImportUnrelatedBrowserPayloads`, followed by a passing isolated run and a 79-case group rerun. That failure is not reclassified as success.

Inspection confirms that `MappingAllowance` measures elapsed time while checked evaluation is active. It includes first-use/JIT work and scheduling delays; it is not a CPU-time clock. The failing fixture adds a 1.2 MB *unreferenced* observation. The new cold/warm measurements and complete Flow suite verify that unrelated payload size does not affect charged materialization or statements. There is no runtime time-limit, accounting or test-timeout change.

The original stall has not reproduced here, and available evidence does not identify its exact scheduler/JIT cause. Passing repeats alone are not claimed as a race fix. Full-suite and candidate-CI outcomes remain required independently of those repeats.

## Failures encountered during implementation

- The first full-solution run reused earlier Planning assemblies after concurrent guidance edits: 5,486 passed, four prompt-assertion failures, 13 skips. It is retained as a failed mixed-build run, not final acceptance. The isolated frozen `4b87827d` rerun was later stopped after the identity audit proved that candidate needed the additional conservative compiler guard. Final acceptance requires a complete fresh run on the corrected source.
- Initial longer description guidance exceeded the retained compact-discovery request budget. Redundant wording was shortened while preserving the rule and existing assertions; all discovery checks pass at the unchanged limit.
- Candidate CI on `0efb485c` caught two capability-clarification regressions: compacting the prompt had removed the explicit "compatible alternatives" instruction. The instruction was restored in production guidance; the existing assertions were kept. The initial failing CI log remains part of the evidence, independently of subsequent results.
- The first local heading-free fixture added enough page text to exceed its existing mapping request allowance. The integrated fixture now uses one complete heading-free block; the separate retained-data replay covers all five offered blocks. No production allowance changed.
- A first test graph put an inline reference array into a lookup declaration and encountered the existing graph-node parenting restriction. The regression now uses the normal typed ID-array producer used by the compiler. No validator or accepted-plan rule changed.
- The first lookup-helper assertion counted both an output and the document's mirrored public export. It now checks the actual single output expression; both strict lookups remain required.
- The default Homebrew Python lacks PyYAML. The skill validator passed with the existing system Python; no dependency installation or repository change was needed.
- Compact-prompt assertion edits inadvertently changed punctuation in the separate runtime-objective assertion. The local deterministic adapter threw and reconciliation correctly blocked cleanup. The original objective assertion was restored; bound-input and nullable-ID assertions now match their equivalent compact wording. All 26 local business/nullable-ID cases pass without changed workbook or recovery assertions.
- A replay-tool restore mixed `/tmp` and canonical `/private/tmp` project paths and collided on `project.nuget.cache`. Canonical references and serial build succeeded with zero warnings; neither historical data nor production behavior changed.

No failure triggered a paid call, workflow replay, historical edit or relaxed oracle. PR #117 remains draft; future paid planning and live execution require new authorization and artifact approval.
