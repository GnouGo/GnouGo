# Business commitments: authorized Amazon and code planning

**Both fresh live planning sessions stopped before review. Neither produced YAML
or an approvable artifact, and neither workflow executed.** This is failed live
planning evidence, not an E2E pass. PR #117 remains draft.

The user authorized Amazon and code live testing with “can you run amazon and code
live test? go for it.” We used validated candidate
`b50474fdb414ef22e265a45718d012257f0a9e9b`, intent version 3, compiler v15 and
automatic correction, with fresh identities under the existing campaign. Each
workflow still required separate revision/hash-bound approval and explicit
requirement acknowledgments; neither reached that boundary.

| Scenario | Run | Final state | Logical calls / HTTP attempts | Corrections |
| --- | --- | --- | --- | --- |
| Amazon, at most ten products | `commitments20261017a-amazon-1` | stopped, revision 3 | 2 / 3 | 0 |
| Pinned SmartGuide PR #610, local feedback only | `commitments20261017a-code-1` | stopped, revision 3 | 2 / 2 | 0 |

## Preparation and frozen boundaries

- All 32 applicable candidate CI checks passed; four conditional jobs were skipped.
  The preceding serial validation had 5,914 passing tests and 12 platform/opt-in
  skips, plus frontend/browser, package, Native AOT and encrypted-recovery checks.
- A fresh harness build passed with zero warnings/errors. All 631 frozen binary
  files and the clean candidate were checked before paid planning. No production
  source changed during these tests.
- Pricing/currency readiness passed. The initial committed/reserved upper bound
  was €151.347264 / €200; all historical reservations remained included.
- Actual local Browser navigation, independent XLSX readback and Browser closure
  passed with zero inference. These are readiness checks, not Amazon outcomes.
- Git, Cmd, Copilot and Document discovery succeeded. The pinned SmartGuide head
  and base remain available. Disposable Node 24.20.0, pnpm 10.34.5 and Python
  3.11.13 match the repository's manifests. Dependency installation, command
  permissions and actual code-check success remain untested by this readiness.
- Only repetition one of each scenario was authorized and attempted. The harness's
  historical six-slot manifest does not authorize or imply additional repetitions.

## Confirmed failure and routing gap

All four committed responses pass their **original issued strict JSON schemas**
on read-only receipt inspection. Structured output establishes shape, not the
meaning of a result commitment.

Amazon's first proposal declares public outputs `success`, `workbookPath`,
`status` and `rowCountIncludingHeader`. Its writer commitment instead says:

```json
{
  "operation": "cap_37c513d225bb1b870053c5d3",
  "result": {
    "port": "filePath",
    "output": "workflows/schema-portability-20261002/commitments20261017a-amazon-1/products.xlsx"
  }
}
```

`result.output` must name a declared public output, not a path value. The declared
writer port `filePath` exists; the proposed destination string is not a declared
public-output name. Validation correctly reports `REQUIREMENTS_COMMITMENT_INVALID`
at `/requirements/outcomes/3/commitments/0`.

The code proposal repeats the same semantic mistake five times. Its public outputs
are `reviewJsonPath`, `decision`, `findings` and `commands`, while the commitments
name `workspace.directory`, `commands/codes evidence`,
`revision.comparison.files`, `local diff findings`, and
`write_file review.json then delete_directory repository`. These are artifact
descriptions or internal-result labels, not the declared public-output names.
The corresponding five commitment locations are rejected.

The safety rejection is correct. The **autocorrection omission is a planner
routing gap**: `HybridWorkflowPlanner.Corrections.cs::InitialProposalDefect`
recognizes missing/invalid initial requirements and interfaces, but omits the new
`REQUIREMENTS_COMMITMENT_INVALID` diagnostic. Neither session accepted requirements
or a TaskPlan. Both therefore stop with six logical calls and both correction
attempts still available. No diagnostic-driven correction was issued.

The retained data demonstrates this omission; it does not prove that a model
would successfully repair the proposal within the remaining allowance.

## Independent proposal observations

Changing only commitment names would not establish business acceptance:

- Amazon proposes consent interaction, learned row assembly and learned final
  status. Its extraction/selection must still be reviewed for complete per-page
  coverage, strict offered/original ID reconnection and exact original values.
  `workbookPath` is exported through interpretation, so merely naming it in a
  commitment does not establish direct writer provenance.
- Code uses real clone/check/review operations and includes report writing and
  cleanup in `always`. However, proposed `findings` and `commands` arrays have
  `maxItems: 0`; root exports also differ from the proposed public interface.
  Report assembly asks interpretation to produce base64 text. These are further
  proposal concerns, not observed execution failures or completed compiler checks.

Requirements validation stopped before TaskPlan acceptance. There is no full
compilation, coverage, workbook, code-check or cleanup acceptance result for either
proposal. No requirements were acknowledged and no approval was submitted.

## Accounting and preservation

| Measurement | Amazon | Code |
| --- | ---: | ---: |
| Planning elapsed milliseconds | 380,785 | 86,386 |
| Verified input tokens | 19,823 | 28,748 |
| Verified output tokens | 6,114 | 7,122 |
| Known estimated cost, EUR | 0.252692 | 0.319649 |
| New uncertain-attempt reservation, EUR | 1.308506 | 0 |
| Runtime inference calls | 0 | 0 |
| Workflow MCP operations | 0 | 0 |

Amazon request 2 completed through the existing bounded transport policy with
history `[unknown, 200]`. Its committed response and the unknown attempt's full
reservation both remain. There was no manual resend. All code transport attempts
returned 200. Code discovery additionally read `copilot_task_contract`; that
read-only discovery call is recorded separately from workflow execution.

Total new known estimated cost is **€0.572341**. Including the retained unknown
reservation, the campaign upper bound is now **€153.228111 / €200**, leaving about
**€46.77**. Estimates use the configured model prices and the admitted ECB quote;
they are not invoices. No ceiling or allowance was increased or reset.

Historical workflows, approvals, receipts, reservations and verdicts were not
changed. The two new stopped sessions remain stopped. Neither execution was
started, so there are no Amazon visits, business workbook, clone, code checks,
report file or live cleanup verdicts. Private readback uses public encrypted-storage
APIs; raw prompts, host contracts, transport bodies and credentials are excluded
from the committed evidence.

## Next minimal correction, not performed by this live test

1. Route invalid **initial** commitments through the existing bounded initial
   regeneration when no requirements/plan are accepted and no request is pending.
   Do not permit automatic changes to already accepted commitments.
2. Make the diagnostic identify `result.output` and the declared public-output
   names. Replace ambiguous fresh guidance with one complete operation-only versus
   producer-port/public-output example. Never infer a replacement from types or
   silently remove a commitment.
3. Replay both saved responses without inference; prove one accounted correction
   can reach validation, and repeated invalid proposals stop. Preserve historical
   authority, receipts, uncertainty and the existing single structural fallback.
4. Review the remaining composition defects independently, then rerun the owning
   and integrated gates before considering another authorized live attempt.

This report does not fix production code, revise either stopped session or
authorize another paid attempt. The new defect has a small routing surface, but
fixing it alone does not prove future model compliance or business acceptance.

## Evidence

- [Freeze](freeze.json), [authorization](authorization.json),
  [candidate CI](candidate-ci.json), [readiness](readiness.json),
  [provider readiness](provider-readiness.json) and [binary hashes](binary-hashes.json.gz).
- Amazon: [proposal](amazon-proposal.json.gz),
  [proposed requirements](amazon-requirements.json), [diagnostics](amazon-diagnostics.json),
  [requests](amazon-requests.json), [result](amazon-result.json),
  [outcomes](amazon-outcomes.json), [log](amazon-planning.log).
- Code: [proposal](code-proposal.json.gz),
  [proposed requirements](code-requirements.json), [diagnostics](code-diagnostics.json),
  [requests](code-requests.json), [result](code-result.json),
  [outcomes](code-outcomes.json), [log](code-planning.log).
- [Campaign accounting after both tests](accounting-after.json).
