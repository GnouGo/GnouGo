# Execution identity and runner readiness

The retained `CoedeurTortue` execution (`487579e92e6c40427c7e38024e06efa4`)
failed before Copilot inference. Its receipt reports missing mandatory sandbox
policy and zero agent model calls. Cleanup independently failed because finalization
cleared `RunId`, although tenant `default` was present. The generated cleanup also
selected a different directory from clone/review; its verification named the clone
directory. The sandbox refusal correctly prevented unconfined command execution.

## Changes

- Finalization retains host identities, distinct invocation paths and its independent
  cancellation/step limits. Primary and cleanup failures remain separate.
- Copilot discovery reads validated managed policy without creating a task, invoking
  inference or executing commands. Its existing runner schema reflects the permitted
  capabilities/evidence kinds. Missing, invalid or unreadable policy fails closed.
- Configuration is rechecked at task validation; actual session sandbox enforcement
  is still mandatory. All provider policy parsing stays in the Copilot integration.
- The additive optional `AgentTaskResult.Failure` uses the existing `WorkflowError`
  contract and source-generated serialization. Host-authored safe diagnostics survive
  receipts and runtime reporting; arbitrary exception/model content is not displayed.
  Old results retain their fallback and unknown outcomes require reconciliation.
- Generation guidance prefers declared deterministic operations for fully specified
  actions. The compiler does not infer that independent paths describe one resource.

TaskPlan, planning format 10, execution journal schema 9, permissions and budgets
are unchanged. No settings, saved workflow, approval, original receipt or clone
was modified. There is no new planning phase or provider-specific Flow rule.

## Evidence and validation

[Sanitized original receipts](../tests/GnOuGo.Agent.Server.Tests/Fixtures/RunnerReadiness/retained-execution.json)
retain both errors and the resource mismatch. The originals were read through public
KeyVault APIs and verified unchanged. Historical evidence is not a corrected proposal.

Five new finalization tests failed before the fix and passed after it. Further
regressions cover secondary failure preservation, cancellation, nested workflows,
recovery without redispatch, tenant isolation, safe diagnostics, policy parsing,
read-only discovery, policy changes, enforcement refusal and contract fingerprints.

The explicitly synthetic shared-workspace planner/compiler lifecycle regression
uses one declared location, temporary local Git and real Cmd cleanup. Mocked agent
isolation refusal does not prevent cleanup of the intended target; unrelated/source
files remain intact. This reuses the existing independent lifecycle oracle and is
not a replay of real Copilot execution. Existing approval/scoped-repair tests remain.

Complete deterministic, package, serialization/Native AOT and platform CI outcomes
are recorded on [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113), against its
exact validated revision. Local logs, including unsuccessful checks, are retained
under `artifacts/runner-readiness-2026-09-29/`.

## Deployment and limitations

Deploy the updated runtime and Copilot MCP together, refresh discovery, then
explicitly revise/regenerate and approve. Older strict result readers may reject the
new optional failure field; old encrypted receipts are not migrated or rewritten.

An administrator must configure mandatory managed `sandbox.enabled=true` and
`sandbox.failIfUnavailable=true` and ensure host enforcement works. Discovery reports
configuration readiness, not successful command execution in a future workspace.
The implementation never edits machine policy or enables bypass. Network and
credential restrictions remain, so dependency downloads can still be refused.

Reuse one explicit resource binding for creation, approved workspace and cleanup;
prefer a declared deterministic deletion operation when appropriate. Do not derive a
second location in a transform. No compiler heuristic automatically rewrites paths.

No paid inference, real Copilot task, external workflow or automatic cleanup was run.
Preserve the failed execution and existing clone. Passing these tests does not
establish successful real Copilot execution. Keep PR #113 draft; do not merge.
