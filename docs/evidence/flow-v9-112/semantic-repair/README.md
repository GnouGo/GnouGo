# Deterministic semantic diagnostics and bounded repair

This follow-up continues from `c0c3c03` on `feat/flow-hybrid-planning-v9`. Implementation source: `1e1d2ee4c6c97a46b6645cc1dc4bc364ee850615`. It reproduces and repairs the retained parallel-output failure through deterministic tests. **No paid/live planning benchmark or real Copilot task was dispatched.** The historical live result remains [8/9 at `d636c99`](../taskplan-stabilization/README.md); these changes make no new live-performance claim.

## Changes and boundaries

Semantic preflight runs inside `TaskPlanCompiler.Compile`, before graph nodes are emitted. It uses transient source/ownership indexes over the existing TaskPlan and shares value, schema and contract checks with lowering. Public contract shapes, planning format 10, execution journal schema 9, runtime, integrations, permissions and persistence are unchanged. There is no additional model phase or persisted representation.

Preflight collects independent input, identity, dependency, business-value and scope-output diagnostics in deterministic location/code order. Invalid producer contracts suppress downstream type errors without hiding independent failures. Parent consumers require explicit child exports; available ancestor captures remain supported, and parallel siblings cannot directly consume one another. Generated-plumbing failures stop as compiler diagnostics.

Repair permissions cover diagnosed business slots and complete explicit export chains. Revalidation does not authorize edits to dependent tasks. Added exports must connect diagnosed producers to the affected consumers; their names and values remain model-authored TaskPlan intent. Conditional alternatives require explicit matching declarations. The compiler invents no export, default or fallback. Unrelated declarations, task structure, concurrency, ordering and composite members remain fixed. Rejections preserve the baseline, discovery receipts and cumulative budgets, including after recovery. Unmapped or ambiguous diagnostic locations grant no broad permission.

Implementation commits:

- `70d9924`: compiler semantic preflight, source/ownership bookkeeping and semantic regressions.
- `bc544bb`: complete export-chain repair permissions, atomic rejection and recovery regressions.
- `6d69fcf`: fail closed when distinct business identifiers produce an ambiguous diagnostic path; remove an unused compiler traversal.
- `1e1d2ee`: portable repository skill and consolidated current documentation.

The new `.agents/skills/gnougo-planning/SKILL.md` uses standard name/description frontmatter without agent-specific configuration or tool preauthorization, following the [Agent Skills specification](https://agentskills.io/specification) and the supported [Copilot repository location](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/customize-cloud-agent/add-skills). It freezes architecture, model/compiler boundaries, scoped repair, approval/evidence safety and deterministic development. It forbids paid/live benchmark iteration toward a passing result.

Removed: first-error-only semantic input checking; the unused compiler scope traversal; transitive whole-task edit authorization; the unmapped-diagnostic whole-plan fallback; and active documentation for removed free-text decision/scope-consent APIs, binding batches and computation inference. Current architecture and migration guidance replace contradictory graph-planner descriptions and expired campaign instructions. Historical reports and encrypted evidence remain unchanged.

## Regression evidence

The sanitized retained-case reproduction places three checks inside parallel branches without exports and references six internal producer ports from root outputs. Preflight now reports all six consumers plus all three missing export boundaries together, even alongside an independent invalid input; the returned graph and generated source map are empty. An explicit minimal correction passes the unchanged independent nominal, failed-check, incomplete, rejected-confirmation, changed-revision, cancellation, workflow-denied and permission-unavailable execution rules.

| Regression step | Result |
| --- | --- |
| Initial retained-case and permission tests | 3 failed / 4 passed |
| Semantic preflight | 2 failed / 5 passed; complete diagnostics now pass |
| Updated slot permissions and retained oracle variants | 164 planner tests passed |
| Duplicate choice identity | Reproduced an exception; now fails closed with semantic diagnostics |
| Nested exports, conditional counterparts and recovered rejection | 169 planner tests passed |
| Ambiguous business diagnostic path | Reproduced excess edit permission; now grants no permission |
| Final planner suite | **170 passed / 0 failed** |

Existing scope-shape assertions were updated from whole-task identifiers to precise business locations. Their behavioral assertions remain, with stronger checks that consumers and unrelated descendants cannot be edited. Shared corpus requests, operation metadata, benchmark harness and independent oracles are byte-for-byte unchanged. All 57 prior evidence files match their retained hashes.

The full local .NET solution passed **2,877 tests across 33 projects**, with zero failures and five existing opt-in live skips. Python passed **287 core + 27 CLI + four script tests**. Skill validation and local documentation links passed. Five independent Flow packages passed metadata/build checks. All eight deterministic Native AOT planner scenarios passed locally and in CI. The published-server planning persistence check passed. Package and Native AOT results, diagnostic audits and completed CI are recorded alongside this report.

Initial unsuccessful attempts are retained in the regression records and local log hashes. One intermediate test edit failed compilation by assigning an init-only record property; the test now replaces that record. The system Python lacked PyYAML for the skill validator; the existing core-package virtual environment validated the skill successfully without installing dependencies or changing the skill. No production warning was suppressed to pass validation.

## Completed CI

All five applicable workflows completed successfully on implementation commit `1e1d2ee4c6c97a46b6645cc1dc4bc364ee850615`: **32 successful checks**, no failures, and four release-only skips. No required dependent job was skipped. The skipped checks are release publication, the release-only ProxyCopilot job, PyPI publication and NuGet publication. All five downloaded workflow logs passed the compiler-warning audit.

| Workflow | Result |
| --- | --- |
| [Main build, published-server persistence and six desktop platforms](https://github.com/GnouGo/GnouGo/actions/runs/36118254418) | Passed |
| [Deterministic planner validation and Native AOT](https://github.com/GnouGo/GnouGo/actions/runs/36118253724) | Passed |
| [Frontend and Python dependency validation](https://github.com/GnouGo/GnouGo/actions/runs/36118253780) | Passed |
| [ProxyCopilot Native AOT platforms](https://github.com/GnouGo/GnouGo/actions/runs/36118253728) | Passed |
| [ProxyCopilot standalone validation](https://github.com/GnouGo/GnouGo/actions/runs/36118253769) | Passed |

Existing desktop CI includes an embedded local Qwen structured-output smoke. That check passed; it is separate from the prohibited paid planning campaign and real Copilot task evaluation. The latter remain undispatched in this pass. A reporting-only follow-up commit can trigger another CI run; implementation validation above is pinned to the frozen source.

## Evidence index

- [Exact source, independent oracle hashes and zero live dispatches](verification-source.json)
- [Regression progression, including failed iterations](regression-progress.json)
- [Full local test counts and warning audits](validation.json)
- [Package and Native AOT commands/results](final-package-smoke-results.json), [independent package dependencies](package-boundaries.json)
- [Documentation links and skill specification](documentation-validation.json)
- [Completed CI checks](ci-checks.json), [workflow/job results](ci-runs.json), [CI log hashes and warning audit](ci-log-proofs.json)
- [Retained local log/TRX hashes](local-log-hashes.json), [unchanged prior evidence hashes](prior-hashes.json), [this report bundle's hashes](evidence-hashes.json)

Raw deterministic logs and TRX files remain in the local ignored `artifacts/taskplan-semantic-repair/` directory; their hashes are committed here. Published GitHub workflow results are linked below. There is no new encrypted model evidence because no live planning or Copilot task was dispatched.

## Remaining limitations and stopping rule

Semantic validation and simulated execution do not establish external success. Invalid sibling references, opaque field access and ambiguous repair locations continue to fail closed. A safely bounded repair can still be impossible; no whole-plan fallback is provided.

Real Copilot command edit/test execution remains unverified under the available mandatory sandbox enforcement. The limitation is documented; no policy bypass, broader permission or live task was attempted. PR #113 remains draft and is not merged.

After deterministic validation and CI, delivery is limited to evidence and the PR description. No further implementation or paid evaluation is part of this pass.
