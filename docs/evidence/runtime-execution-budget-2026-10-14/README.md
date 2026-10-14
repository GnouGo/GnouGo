# Agent.Server runtime execution budgets

Baseline: `bc3a9bc9`, PR #117. This correction uses deterministic model adapters,
disposable stores and local networking only. It does not execute Amazon20, alter
its YAML/approval, replay historical requests, or change any financial allowance.

## Cause and correction

The saved workflow declares two adaptive mappings. Core correctly rejects an
absent cumulative runtime budget before workflow operations, but Agent.Server
never registered its existing optional budget factory. SmartFlow then presented
the thrown `LLM_BUDGET_UNVERIFIABLE` as `INTERNAL_ERROR` in an inappropriate
Designer-revision question.

The host now validates optional `Flow:Execution:LlmBudget` configuration at
startup and registers a small implementation of the existing factory. There are
no default limits. New root runs get independent scopes; nested mapping/LLM work
shares the root allowance. Current configuration never replenishes a saved run.
Initial execution and recovery receive the existing exchange-rate provider.

The existing error formatter preserves the runtime code and safe dispatch,
stage, classification and limit diagnostics. Unhandled budget failures, including
routed failures, do not offer Designer revisions. Explicit handwritten error
handlers retain their declared successful outcomes; genuine workflow errors
still use the existing revision path.

No compiler, mapping executor, YAML contract, journal schema, public HTTP DTO,
planning prompt or budget enforcement changed. The shipped configuration grants
no execution allowance. Operators must explicitly configure finite limits and
restart the host; this delivery does not configure their production instance.

## Focused evidence

- 27 new host/configuration/accounting/recovery checks pass with `-warnaserror`, including invalid nested/scalar configuration.
- A focused run passed 63 checks: the first 20 new host checks, 39 existing
  SmartFlow/error/configuration/interruption checks and all four new local
  Browser/Document scenarios. The final three host regressions additionally
  verify routed errors, old unbudgeted runs and explicit handwritten handlers.
- Invalid explicit policy fails startup. Missing policy causes zero model calls,
  zero workflow MCP effects and no revision prompt. Nonadaptive handwritten YAML
  remains executable without adding a budget.
- Concurrent executions have separate allowances; a child model call cannot
  replenish its parent. Monetary admission succeeds with deterministic pricing
  and exchange evidence, and rejects missing pricing/rates before model dispatch.
- An injected crash after a committed model response preserves usage. Restart
  under a larger current host allowance still enforces the original one- or
  two-call limit and never repeats the first response. An old unbudgeted adaptive
  run stays blocked after a new host policy is configured.
- Unknown model completion retains its reservation and prohibits replay and
  cleanup on restart. No historical uncertain request was used in these tests.

## Independent workbook and recovery evidence

[Local measurements](local-workbook-results.json) come from the actual v15
composition, executed through production host DI and SmartFlowService with an
explicit two-minute **test-only** elapsed budget. The earlier direct-engine
fixture is retained; this adds coverage for the previously missing host path.

Each scenario visits ten local products and checks 39 complete observation pages,
12 offered candidates, exact original action arguments, supplied headers and all
44 workbook cells, including whitespace and literal formula-looking strings.
Complete, repeated-selection and recorded-redirect cases pass the unchanged
observed-products oracle. Altered descriptions are rejected. Browser cleanup is
verified independently.

The missing-facts scenario checks the exact blank cells against independently
controlled fixture content. The unchanged live oracle conservatively rejects
those absence claims; this expected negative verdict is retained, not relabeled
as a live success.

Each successful scenario restarts from encrypted storage through
WorkflowRunService under a different current host policy. Saved limits and
outputs remain identical, workbook bytes do not change, and inference,
navigation and writing are not repeated.

The complete case uses 23 deterministic inference responses, including 11 mapping
generations; typed copying/row construction uses zero inference. Its decision
request is 2,692 bytes, YAML is 187,476 bytes, and it has 143 recorded invocations.
Recorded allocation totals cover the process, not peak memory; this change makes
no compiler or performance-improvement claim.

## Published-host correction

The first trimmed-host smoke rejected valid supplied limits because reflection
binding no longer populated the immutable budget record. Debug tests did not
expose this. Configuration now reads the existing scalar fields explicitly,
checks their structural shape, and delegates limit validation to the unchanged
Core type. No linker suppression or default allowance was added.

The corrected managed/partially trimmed Server publishes with `-warnaserror`.
Actual startup with all four limit dimensions, HTTP health, the UI, Blazor
negotiation, invalid-policy rejection and published encrypted planning persistence
pass without model calls or workflow execution. The existing publish verification
script now includes the explicit-policy startup case. See
[published smoke evidence](published-host-smoke.json).

The initial full-solution run was deliberately superseded after discovering this
publish defect. Its partial log is retained and is not counted as a passing gate.
The initial Flow Native AOT invocation also promoted the two pre-existing EF
experimental-tooling notices to errors; the supported publish verification uses
the repository's existing exact-notice policy. Neither event weakened an assertion
or changed a limit. Failed logs are retained in `failed-attempts/`.

## Full validation and deployment boundary

Full serial solution, supported publishing/recovery and candidate CI results are
reported in the final PR validation comment after completion. Pending checks are
not counted as passed here. Agent.Server's supported publish remains managed and
partially trimmed; applicable Flow tools retain Native AOT smoke coverage.

[Amazon20 hash evidence](retained-workflow.json) confirms the retained YAML is
unchanged. No model-provider request, paid planning, Amazon execution, archival,
approval or financial-state mutation is included. PR #117 remains draft; live
business acceptance is not established by these deterministic fixtures.
