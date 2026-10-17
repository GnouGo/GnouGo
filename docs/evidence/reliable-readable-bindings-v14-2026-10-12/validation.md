# Validation record

Implementation baseline `4d43b222`; mapping `3e2414af` / `3ad3746b`; compiler `f4f9c699`; additional historical/exact-value coverage `dda50b42`. Evidence-only files do not change production behavior. All work here is offline; historical workflow execution, financial reservations and verdicts remain untouched.

## Sequential owning gates

| Gate | Result |
|---|---|
| Mapping reliability | 172 owning mapping checks passed with `-warnaserror`; full retained 54-page replay made zero provider/MCP calls. |
| Emitted planning/repair guidance and discovery | 100 focused checks passed with `-warnaserror`, preserving request budgets. |
| Planning suite | 1,308 passed with `-warnaserror`. |
| Final equivalence additions | 69 fusion/nullable-capture cases passed, including v14 exact numbers, object identity and reserved-name collisions. |
| Actual generated lexical expressions | Four cases passed: exact text, selected null, invalid unused value and missing value; same reached checks and first errors. |
| Local integration | 11 cases passed using real local Browser/Document, independent XLSX inspection and completed receipt recovery. |
| Fresh-session business fixtures | All 18 variants passed after updating the fresh-profile assertion from v13 to v14; workbook/recovery assertions are unchanged. |
| Skill | Existing `skill-creator/scripts/quick_validate.py` passed for `.agents/skills/gnougo-planning`. |

Machine summaries are in [validation](validation/). Complete source/target coverage, limitations, raw compilation measurements and independently inspected workbook artifacts are linked from the [main report](README.md).

## Full solution and candidate CI

The final full-solution command is:

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
```

Its final log is `/tmp/gnougo-v14-solution-final.log`; the initial failed run remains `/tmp/gnougo-v14-solution.log`. Final per-project totals and the exact-head CI verdict are attached to the delivery update on [PR #117](https://github.com/GnouGo/GnouGo/pull/117). An in-progress result, pre-existing skipped test, cancelled job or green non-blocking parent without its child-step result is not a pass. No timeout, assertion, test selection or production allowance was widened to clear this validation.

The existing macOS skips include platform-specific Cmd checks and the explicitly disabled designer-browser UI test. They are reported separately from passing checks. Candidate CI covers supported platform packages and all applicable jobs; no code-review evaluation or paid benchmark is part of it.

## Release, Native AOT and encrypted recovery

An isolated checkout at `dda50b42` produced all five independent Flow Release packages with `-warnaserror` and the `osx-arm64` self-contained Planning Native AOT smoke with `-warnaserror`. The smoke executes compiled planning cases, exact typed values, metadata/recovery and cumulative adaptive mapping cases. [Commands and exit codes](validation/publish-results.json), [native smoke output](validation/planning-smoke.log).

CLI and Server use the existing `verify-warning-free-publishes.ps1` publish profile: Native AOT, trimming, single-file, skipped frontend build/model metadata generation. The exact two EF Core Tasks **10.0.12** experimental notices are the only publish diagnostics; the repository's pinned audit explicitly permits those notices. No suppression or dependency was changed. [Audited notices](validation/audited-publish-notices.txt).

The native binaries pass:

```sh
python3 scripts/verify-flow-v9-published.py \
  --cli /tmp/gnougo-v14-publish/Cli/GnOuGo.Flow.Cli \
  --server /tmp/gnougo-v14-publish/Server/GnOuGo.Flow.Server \
  --data-directory /tmp/gnougo-v14-publish/recovery-data
```

[Recovery output](validation/encrypted-recovery.log): encrypted receipts, native EF query/index reconstruction, retained results, tenant/revision checks, streamed durable human answers and restart all pass; workflow content is absent from database plaintext.

## Retained failures and corrections during validation

- The first wording exceeded the compact-observation discovery fixture's existing allowance. It was replaced with concise equivalent instructions; no budget or assertion changed.
- The broader Planning run exposed an alias-reuse defect: an eliminated declaration name could be remembered as a scalar address. Reuse now records actual member addresses only; the full Planning suite and missing/unused-value cases pass. The initial log remains `/tmp/gnougo-v14-planning-full.log`.
- A coupled-schema test compared a pre-v13 step boundary against v13's already-authorized output timing. The coupled-schema case now holds that independent boundary constant; genuine historical/current execution equivalence is covered separately.
- The first measurement harness attempted Core internals through Planning's test friendship and failed compilation. The allowance measurement moved to the existing Core test assembly; no production visibility changed.
- A publish launch initially used the working checkout; it was interrupted and rerun in the isolated checkout. The first CLI attempt additionally promoted the two audited EF notices with `-warnaserror` and exited 1. The supported repository profile passed with exactly those notices; both outcomes remain in the command record/logs.
- The separate workbook inspection initially compared relative writer arguments with absolute returned paths. It now checks the documented relative/absolute correspondence and workspace location, while retaining exact cell and visit assertions.

- The first full solution exposed a stale fresh-session v13 expectation in the existing business fixture. It now explicitly expects v14. All 18 real-execution variants passed before restarting the full solution; historical-profile checks remain unchanged.

None of these failures consumed paid inference, resumed the retained workflow or changed an oracle to accept fabricated business data. Further live planning and execution remain outside this correction and require new authorization and concrete artifact approval.
