# Planner v2 construction validation — 2026-09-07

This change removes model-generated decision indices from early-reviewed construction.
Approved explicit cases and defaults remain separate. Executable construction now uses
persisted units of at most four nodes, contract resolution before implementation, exact
node keys, bounded field repairs, and deterministic runtime confirmations and public
output bindings. The configured model is unchanged; Agent.Server defaults to low
reasoning and displays the effective configuration and unit progress.

## Defects found and corrected

- An extra default-case index previously threw during conversion before repair. Response
  validation now precedes conversion, and control-flow indices are not editable fields.
- Oversized unit and repair contexts repeated unrelated producer schemas and helper
  bodies. Construction now supplies scoped contracts, splits unattempted oversized
  units, and pauses before dispatch if a single contract exceeds its configured limit.
- Dispatch errors could overwrite candidate findings. They now have separate persisted
  diagnostics; recovery retains the candidate's findings and rejected-attempt history.
- Public outputs could select optional or undeclared producer fields. The response now
  selects a deterministic reference from exportable producers; its schema is derived.
- Nested template references could be incorrectly classified as workflow references.
  Exact source enums and executable validation now reject undeclared workflow names.
- Recursive JSON Schema alternatives reset the instance path and could skip nested
  validation through the recursion guard. Core now preserves the path through `anyOf`,
  `oneOf`, and conditional checks. Regression tests cover nested invalid references.
- Template object/array bindings now lower their nested data references as expressions.
  A deterministic runtime test executes the resulting YAML.

No producer-side MCP discrepancy was established by this campaign. No provider, tool,
repository, or request-name correction rules were added.

## Local verification

Fresh affected test suites passed:

| Component | Passing test cases |
|---|---:|
| Flow.Core | 1,336 |
| Flow.Planning | 139 |
| Flow.Integrations | 53 |
| AI.Core | 176 |
| Agent.Server | 328 |
| Cmd.Mcp / Git.Mcp / GithubCopilot.Mcp | 41 / 48 / 105 |

The Agent.Server frontend build, macOS ARM64 planner Native AOT publish and execution,
and trimmed single-file Agent.Server publish and encrypted EF persistence smoke test
passed. Invoked builds and publishes produced no compiler, trimming, or AOT warnings.
The local persistence publish skipped bundled tool staging; GitHub Actions supplies the
separate full platform builds. Opt-in live tests returning without activation are not
evidence of live generation success.

## Live acceptance remains blocked

Six resume attempts used the explicitly authorized existing KeyVault configuration,
the unchanged configured model, and low reasoning. The user's answer and exact behavior
approval remain retained. No new answer, behavior approval, or final approval was
submitted on the user's behalf.

The retained session reached revision 76 with 46 validated construction checkpoints
under the validators then in use. It remains in recovery at public output construction.
A later read-only compile inspection found two additional misclassified template
references. The final stricter validator and deterministic public-output binding changes
have local regression coverage, but have not completed a subsequent paid live run.
These checkpoints therefore do not establish a valid final executable artifact.

The existing cumulative ledger reached **120 of 120 model calls**. Its recorded estimated
cost is **EUR 68.183557**, including the existing prior-cost reserve; four unresolved
request reservations retain **EUR 31.285493**. The conservative available balance under
the unchanged EUR 100 ceiling is **EUR 0.530950**. No ledger was reset, no unknown request
reservation was released, and no provider-side hard cap was claimed as verified.

The three-session generation/save/execution campaign stopped at its exhausted-limit
preflight before dispatch or external fixture work. **Zero of three required live runs
completed.** No timeout was observed in this turn's successful model requests, but this
does not establish that low reasoning prevents timeouts or that final generation works.

Full live acceptance requires remaining budget and call capacity, then validated
generation, exact artifact saving, execution, and cleanup. Recovery, model responses,
local fake execution, and successful CI are separate checks and cannot replace that gate.

## CI follow-up

The first full CI run passed all 21 build/test jobs, but its macOS logs exposed an
upstream .NET Apple cryptography archive debug-information defect. The follow-up
publish boundary removes only that archive's debug information from a local copy,
retaining code and link symbols. Both macOS CI targets now execute the published
planner and certificate-chain smoke. See the [framework workaround](../tests/GnOuGo.Flow.Planning.Smoke/README.md).
