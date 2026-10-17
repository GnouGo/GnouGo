# Native Copilot list contracts

The `CodeurGithub2` failure in run `c0bb002f493e751ad7ddc4e991bcf650`
occurred before the failing Copilot session was created. The workflow supplied an
object containing `commands` and `scope`, encoded inside `permissionAllowlistJson`.
The published parameter schema accepted any string, while the MCP implementation
deserialized that string into `List<string>`. Replaying the saved value reproduces
the exact root/byte-1 exception without a model call.

The [sanitized invocation](../tests/GnOuGo.Agent.Server.Tests/Fixtures/CopilotLists/retained-failure.json)
preserves the failing permission value, old field schema and exception. Business
prompt and paths are replaced. This evidence is separate from the historical
33/33 planner benchmark, which remains unchanged and did not cover this failure.

## Producer-owned correction

Six parameters across five tools now expose nullable native arrays of nonblank
strings: `permissionAllowlist`, `availableTools`, `excludedTools`,
`skillDirectories`, `disabledSkills` and `contextFiles`. The MCP discovery filter
publishes the same item constraints enforced by its call filter. Validation runs
before SDK argument binding or session creation and reports structured
`INVALID_INPUT` errors such as `permissionAllowlist[1]: A nonblank string is required.`
Values and serializer implementation details are not echoed.

Removed `*Json` arguments are rejected even if null or supplied beside a valid
replacement. There is no best-effort conversion of command/scope objects, silent
argument removal or permission widening. Null/omitted and empty-list semantics,
first-occurrence ordering and existing case-sensitive/case-insensitive
deduplication are retained. Blank list elements now fail explicitly.

The change belongs entirely to the Copilot MCP producer. Flow planning, its
compiler, runtime, persistence and permission implementation are unchanged.
Callers refresh discovery, revise affected TaskPlans and obtain approval for the
new artifact. Existing pending schemas and stored approvals are preserved.

## Installation/testing workflow revision

Fixing the list shape alone would not make `auto_approve_allowlist` suitable for
installation or writes. `copilot_one_shot` remains non-interactive and its allowlist
remains read-only. `copilot_interactive_one_shot` is the existing operation for
installation, tests, commands and editing, using normal permission callbacks.

The saved `runQualityChecksWithCopilot` task was revised in a separate encrypted
final-review session: use `copilot_interactive_one_shot`, remove `permissionMode`
and the malformed `permissionAllowlistJson`, and preserve its objective, prompt,
attachments, workspace binding, outputs and surrounding TaskPlan. Semantic
compilation, artifact validation and approval-recompilation verification passed
without model calls or external execution. The new review remains **unapproved**;
the original saved session, agent workflow, run and approval remain intact.
Approval rechecks producer contracts, and saving checks the original workflow hash.

Review and approve this revision in the updated Agent.Server before saving/running
it. A persistent grant must belong to CodeurGithub2's own stable agent ID and tenant;
otherwise the operation asks for permission. No grant was created or copied.

## Validation

The final warning-free solution run passed **3,846 tests** across 33 projects,
with **12 existing skips**. Copilot MCP passed 199, Core 139 and Agent.Server 535.
The published Native AOT executable passed all five stdio smoke tests.

- The pre-change solution run passed all **3,813 existing tests**, with **12 existing
  skips**. The 27 newly introduced list regressions failed against the old contracts.
- Native-list transport regressions cover every tool/parameter, matching discovery
  validation, malformed/legacy rejection before any SDK session/send, omission,
  null, empty arrays, order, duplicates and tenant propagation.
- Four generated workflows execute through the real Flow engine with deterministic
  MCP adapters: both one-shot modes and renamed tool/server contracts. They verify
  exact arrays and exported results, compile-time rejection of malformed lists,
  zero planning/repair calls and stale-approval rejection.
- Published macOS `osx-arm64` Native AOT checks exercise real stdio discovery,
  invalid/legacy rejection, valid array binding followed by missing-tenant refusal,
  and the existing four encrypted grant-management checks.
- Existing Core permission tests retain read-only restrictions, interactive grants,
  human questions, workspace confinement and bounded-run restrictions.

These are deterministic integration/transport results, **not live provider execution**.
No paid evaluation, external repository execution or automatic rerun was performed.
Machine-readable totals and final build results are in
[validation.json](evidence/copilot-lists/validation.json); detailed local logs are
under `artifacts/copilot-lists/`.

## Reproduction

```bash
dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~CopilotListPlanningTests|FullyQualifiedName~CopilotAttachmentPlanningTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror

dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 \
  --self-contained true -m:1 -warnaserror -o artifacts/copilot-lists/copilot-aot
GNOUGO_COPILOT_LIST_SMOKE_EXECUTABLE="$PWD/artifacts/copilot-lists/copilot-aot/GnOuGo.GithubCopilot.Mcp" \
GNOUGO_COPILOT_GRANT_SMOKE_EXECUTABLE="$PWD/artifacts/copilot-lists/copilot-aot/GnOuGo.GithubCopilot.Mcp" \
  dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~CopilotListStdioTests|FullyQualifiedName~PermissionGrantManagementTests'
```
