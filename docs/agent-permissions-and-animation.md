# Agent-specific Copilot approvals and nested workflow animation

This follow-up to PR #117 fixes two independently reproduced issues on baseline
`507e042022c8580d070605ff2c3d88ed8fb6103d`.

## Causes and corrections

The MCP editor displayed `EnableApproveAll=true` while the MCP default was
`false`. The editor deliberately omits unchanged defaults, so saving the displayed
value could leave broad approval disabled. Both host configuration templates now
use `false`. Choosing a persistent grant explicitly stores **both** approval gates
in encrypted KeyVault overrides and uses refreshed configuration for management
operations and subsequent workflow runs.

`/mcp edit GnOuGo.GithubCopilot.Mcp` now includes **agent_permissions → Configure
agent permissions**. Select an existing agent by name and stable ID, inspect its
current grant, then choose **Keep current**, **Allow All including sandbox bypass**,
or **Remove persistent approval**. The agent ID is rechecked before mutation;
tenant identity travels in host-owned MCP metadata, not tool arguments.

The new `copilot_permission_grant_create` management tool requires the existing
explicit **Confirm persistent sandbox-bypass approval** answer through MCP
elicitation. Cancellation saves no grant; the enabled host gates remain visible.
Creation is excluded from planning discovery by the existing management metadata.
Grants reuse the encrypted future-agent store, follow renames, survive restart and
are revoked through the editor, existing revoke commands or agent deletion. Only
the selected agent receives a grant. The first subsequent permission callback can
reuse it and emits existing permission audit events.

Business questions remain interactive. Explicit denial modes, workspace and file
restrictions, approved task capabilities and bounded execution remain enforced.
Bounded runs still forbid sandbox expansion. This change does not add browser-side
automatic acceptance or a global automatic-approval setting.

The animation bug arose because `TaskHandedOff` describes both an outgoing call
and its return. A nested return targets a worker too; indexing it as a new call
made the caller its own child, then accessed its unfinished layout. The package
now checks source actor, caller instance and distinct target instance before
indexing children. Event contracts and execution behavior are unchanged.

## Evidence

- Baseline nested layout regressions: **2 failures / 4 cases**, with
  `KeyNotFoundException` in `WorkflowSceneLayoutBuilder.LayoutCall`. After the fix,
  all four pass, including sequential/parallel loops and repeated step names in
  different workflows; all **42 animation package tests** pass.
- [Sanitized saved-workflow structure](../tests/GnOuGo.Agent.Server.Tests/Fixtures/Animation/nested-generated-workflow.json)
  retains the three-level call hierarchy and IDs of the reported execution.
  Scene preparation passes. Business inputs are removed; this is a structural
  replay, not an execution of the user's repository.
- **5 real Flow engine executions** with deterministic local MCP/human adapters:
  sequence, sequential loop, parallel loop, parallel branches and expected
  failure. Agent.Server telemetry drives movement, waiting/resume, handoff,
  return and terminal states. No provider is invoked.
- **4 real stdio MCP tests**, repeated against the published **osx-arm64 Native
  AOT executable**, verify confirmation/cancellation, host gates, encrypted
  persistence across process restart, tenant/agent isolation and revocation.
- Editor regressions cover selected IDs with duplicate names, rename, deletion,
  current-grant display, unchanged policy, removal, confirmation streaming and
  cancellation. SDK regressions verify first-request automatic approval with and
  without sandbox bypass, interactive business questions, audit events and host/explicit denials. Existing bounded
  execution and filesystem regressions remain passing.
- Full solution: **3,813 passed, 12 existing skips, 33 projects**, with
  `-warnaserror`. Relevant projects include **139 Copilot.Core**, **171
  Copilot.Mcp**, **530 Agent.Server** and **42 Animation** passing tests.
- Agent.Server production frontend build and Animation Release package pass.
- Chromium on an isolated Agent.Server instance: real editor → cancel → confirm →
  reread → revoke, including a 430px viewport. Five synthetic real-engine traces
  replay through the shipped browser controller: **299 events**, empty queues,
  terminal completion/failure and no browser errors.

Machine-readable results: [solution and package checks](evidence/permissions-animation/validation.json),
[browser permission flow](evidence/permissions-animation/browser-permissions.json),
[browser animation replay](evidence/permissions-animation/browser-animation.json).
Local screenshots and detailed logs are retained under `artifacts/permissions-animation/`.

## Reproduction

```bash
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
pnpm --dir src/GnOuGo.Agent.Server/ClientApp run build
dotnet pack src/GnOuGo.Assets.Animation -c Release -m:1 -warnaserror

dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 \
  --self-contained true -m:1 -warnaserror -o artifacts/permissions-animation/copilot-aot
GNOUGO_COPILOT_GRANT_SMOKE_EXECUTABLE="$PWD/artifacts/permissions-animation/copilot-aot/GnOuGo.GithubCopilot.Mcp" \
  dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~PermissionGrantManagementTests

GNOUGO_ANIMATION_CAPTURE="$PWD/artifacts/permissions-animation/capture" \
  dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~NestedWorkflowAnimationExecutionTests
```

For browser checks, start a separately configured Agent.Server with disposable
Agent, KeyVault, telemetry, planning and execution stores, distinct loopback ports,
and the current bundled MCP executable. Use the Development environment when
serving an unpublished build so Blazor's development static assets are available.
Create a disposable agent through Agent.Server or the mounted `agent_add` tool.
It must have no persistent grant. Set these environment variables:

```bash
export AGENT_SMOKE_URL=http://127.0.0.1:<isolated-app-port>
export AGENT_SMOKE_NAME='Permission smoke reviewer'
export AGENT_SMOKE_OUTPUT="$PWD/artifacts/permissions-animation/browser"
export GNOUGO_ANIMATION_CAPTURE="$PWD/artifacts/permissions-animation/capture"
# If Playwright is not locally installed, point to an existing installation:
export PLAYWRIGHT_MODULE_PATH=/absolute/path/to/playwright/index.mjs
node scripts/smoke-agent-permissions.mjs
node scripts/smoke-workflow-animation.mjs
```

The permission smoke deliberately creates and revokes a grant for that disposable
agent. Browser animation replay uses captured deterministic execution telemetry;
it is distinct from live provider execution. The frontend package has no separate
lint or type-check scripts; validation uses its production build and host tests.

## Scope and remaining work

No paid provider calls, external repository execution, automatic reconciliation or
resumption of the blocked run were performed. The historical **33/33** benchmark
at `7d5d42b2` is unchanged and does not validate these fixes. Timeout coordination
remains separate. An already-uncertain external invocation still needs an actual
verified completion receipt before it can resume or clean up; configuring a grant
does not provide that receipt. Restart Agent.Server/Desktop to load the new code;
already-running Copilot sessions keep their original configuration.
