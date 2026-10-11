# Fresh Amazon evaluation without consent actions

The user explicitly authorized one fresh Amazon evaluation, selected **stop and
report** if a real consent obstacle prevents progress, and retained the separate
revision/hash-bound artifact approval gate. Maximum: ten products. No code-review
evaluation or cohort expansion is authorized here.

## Scoped change

The original business prompt did not request cookie acceptance. The prior model
introduced it; a later review revision preserved that operation. Candidate
`3c765087` changes only the live scenario's constraints and harness documentation:
no accepting, rejecting or customizing cookies, and no classification of an
informational cookie link alone as a blocker. An observed obstacle preventing work
must remain a failure. Browser, planning, compilation, mapping, permissions,
timeouts and execution oracles are unchanged.

All historical runs, prompts, approvals, receipts and reservations remain intact.
The new cohort is `noconsent20261009a`, run `noconsent20261009a-amazon-1`. Its source
and binaries are frozen in the matching isolated checkout. See
[candidate fingerprints](frozen-candidate.json).

## Deterministic validation and readiness

- **84 targeted tests passed with `-warnaserror`:** 25 Browser action-reference
  tests and 59 host/accounting/approval/observation/local-execution checks.
- The retained local composition covers seven variants, including no consent,
  repeated selections, missing observations, denied writing and a fabricated-value
  oracle regression. Positive cases independently inspect actual XLSX cells,
  product visits and cleanup; expected failures remain failures. This evidence
  does not establish live Amazon success. See [validation](validation.json).
- Host-test and benchmark builds passed with zero warnings/errors. The isolated
  build initially failed downloading the pinned Copilot CLI; an unchanged rebuild
  completed successfully. No SDK, dependency or timeout changed.
- [Provider readiness](provider-readiness.json) resolved the exact pinned model,
  pricing and admitted currency quote with zero inference. The existing 96,000
  input-token allowance and EUR 150 ceiling remain unchanged.
- [Execution readiness](execution-readiness.json) verified an actual disposable
  Browser read, Browser closure and independently read XLSX content. An initial
  invocation used the repository as `--workspace` rather than Document's configured
  workspace: the MCP write succeeded, but the harness checked the wrong location.
  The disposable file was removed and readiness passed with the correct existing
  workspace. [Initial failure](readiness-initial-failure.json) is retained; no
  permission or host-policy change was made.
- [Pre-dispatch accounting](accounting-before.json): EUR **117.920973 / 150**,
  including unchanged EUR **6.501615** conservative reservations. The three
  historical unresolved logical requests have existing retained-inconclusive
  closures; none was replayed or reclassified.

The [baseline CI report](baseline-ci.json) is not green: local workbook fixtures
exhausted their existing elapsed allowance after planning on CI. The same focused
regressions pass locally. No allowance was raised. Production is unchanged, so no
new full-solution/package/AOT run is claimed for this scenario-only modification.

## Planning and execution

**Fresh live planning failed. Amazon execution never started; no workbook was
created and no live execution oracle ran.** No artifact approval was requested or
submitted for a rejected proposal. PR #117 remains draft.

| Stage | Result |
| --- | --- |
| Initial generation, revision 2 | Stopped before execution: repeated port selection and nonboolean `requires`. Review also found raw snapshots entering interpretation, unrequested organic/sponsored filtering and synthetic blocked rows. [Proposal](plan-r2.json), [diagnostics](diagnostics-r2.json). |
| Explicit data-adaptation revision | Preserved requirements and external operations; introduced compact `extract.each` page views and removed the unrequested filtering. Two reversed lookup operands failed validation. Both automatic repairs serialized binding descriptions as business JSON instead of correcting typed operands. [Revision request](revision-r2.json), [retained repair responses](responses-r7.json). |
| Final explicit revision, revision 10 | Lookup ordering and candidate-validation composition were corrected, but five collection concatenations became JSON strings, and two dependencies incorrectly named ancestor tasks. [Proposal](plan-r10.json), [diagnostics](diagnostics-r10.json), [revision request](revision-r7.json). |
| Offline minimal correction | Five `json` kinds changed to existing typed `flatten`; two cross-scope dependency edges removed, retaining their data references. The public compiler and graph validation return zero diagnostics. [Corrected detached copy](offline-corrected-plan.json), [exact changed paths and limits](offline-final-correction.json). **Not applied to the saved session, approved or executed.** |

The [accepted requirements](requirements-r10.json) are unchanged from the first
response, and all six external operation identities/contracts are preserved.
See [review checks](review-checks.json). The generic minimum is typed collection
concatenation rather than JSON encoding, and ancestor data references rather than
cross-scope ordering edges. No compiler or validator relaxation is needed. The
offline copy establishes contract validity only; it does not establish successful
navigation, extraction or workbook creation.

[Final accounting](result-r10.json): **6 logical calls, 8 physical attempts,
2 automatic repairs, 2 explicit review revisions, 2 discovery reads; 74,412 verified
input / 28,748 output tokens; EUR 1.095483; 1,098,023 ms cumulative planning time.**
The physical-attempt and automatic-repair allowances are exhausted. No further
dispatch, new replacement session, accounting reset or uncertain replay occurred.
All six logical requests have responses; this run adds no unknown attempt.

Campaign upper bound: **EUR 119.016457 / 150**, including the unchanged historical
EUR **6.501615** reservations. Runtime inference, Amazon navigation, product visits,
XLSX creation and live cleanup remain **not executed**. The retained six-slot
[cohort report](cohort-report.json) is explicitly incomplete; no other slot ran.

The frozen candidate's [Server CI job](candidate-ci-failure.json) failed NuGet
downloads during publishing. Other jobs were still running at the
[last status capture](candidate-ci-status.json); CI is not reported green. The
local builds and 84 focused tests passed. Any next paid validation requires a
fresh authorized identity and its own concrete artifact approval; this exhausted
run is not reset.

## Reproduction

Run the frozen executable from its matching checkout, using the configured
Document workspace for `--workspace`:

```sh
tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark \
  --schema-portability inspect-run --workspace /path/to/document-workspace \
  --campaign schema-portability-20261002 --run noconsent20261009a-amazon-1
```

Inspection is read-only. Never repeat `plan` for an existing identity or execute
an already-started run. An execution command requires a newly reviewed approval
file; acknowledgments are not populated automatically.
