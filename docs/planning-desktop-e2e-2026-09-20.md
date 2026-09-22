# Real Desktop review validation — stopped at budget preflight

Historical preflight: the user subsequently waived provider-budget verification. See the
[resumed native Desktop test](planning-desktop-protocol-2026-09-20.md) for current results.
The original observations below are retained unchanged.

## Result

The requested real Desktop validation is **blocked before paid execution**. The configured integration does not establish the required EUR 20 aggregate ceiling across planner generation and Copilot inference. No real review or publication succeeded or was attempted in this validation.

Starting source: `583255224532c1ae5b86a645510ed5c0a097b968` on `feat/deterministic-planner-v2`, clean and synchronized with its upstream. Planner behavior remains frozen at `65dc34a5184e1ab118c0edd7676f1b2c3dd23f45`. This report changes no source, public contract, storage format, policy or planner behavior. The [accepted reliability evaluation](planning-default-response-domains-2026-09-20.md) and [offline release validation](planning-finalization-2026-09-20.md) remain separate historical evidence; neither proves real Desktop execution.

The approved test plan explicitly requires stopping if existing host/provider controls cannot verifiably enforce the combined ceiling, and prohibits building a new accounting proxy for this test. That stop condition was reached. Classification: **Desktop/host wiring — budget-enforcement prerequisite**, not a demonstrated planner/model failure.

## Preflight evidence

Source inspection identified these boundaries:

- [CopilotSendResult](../src/GnOuGo.GithubCopilot.Core/CopilotContracts.cs) returns completion, model, events and tool-execution observations, without complete token/cost accounting. The legacy code client exposes only partial usage. [The progress mapper](../src/GnOuGo.GithubCopilot.Mcp/CopilotSdkProgressEventMapper.cs) turns `assistant.usage` into a status message; it does not provide a spending ledger.
- [CopilotInferenceProxyHandler](../src/GnOuGo.GithubCopilot.Core/CopilotInferenceProxyHandler.cs) is an optional forwarding boundary. It delegates ceilings and receipts to an explicitly configured loopback policy host, without direct fallback. It is not itself a budget implementation.
- [MCP startup](../src/GnOuGo.GithubCopilot.Mcp/Program.cs) installs that handler only when `Code:Copilot:InferenceProxyEndpoint` is configured. Its readiness exchange establishes interception availability, not proof of a particular spending limit. See the [integration documentation](../src/GnOuGo.GithubCopilot.Mcp/README.md).
- [WorkflowPlanningBudgetSettings](../src/GnOuGo.Agent.Server/Configuration/WorkflowPlanningBudgetSettings.cs) cannot by itself enforce cumulative spending for the separate Copilot SDK integration. A planner call/token limit is not a combined monetary ceiling.

A temporary read-only console probe used the public `KeyVaultDatabasePathResolver` and `KeyVaultSecretReaderFactory.CreateWorkspaceCatalogReader` APIs. It linked the existing `CopilotKeyVaultConfigurationOverlay.cs` unchanged and inspected the packaged configuration, inherited environment and KeyVault overlay. It did not query SQL directly, dispatch inference, write configuration or print decrypted values. Redacted results:

```text
keyvault_database_exists=True
keyvault_overlay_loaded=True
copilot_proxy_configured=False
copilot_provider_configured=True
copilot_model_configured=True
configured_provider_count=1
copilot_budget_configuration_present=False
copilot_mcp_launch_configuration_present=False
copilot_mcp_launch_proxy_override_present=False
model_requests_dispatched=0
```

The launch-configuration checks examined the canonical and legacy KeyVault entries for `GnOuGo.GithubCopilot.Mcp`. Absence of those entries does not mean the bundled integration is absent. It means no proxy override was found there. No provider-side hard spending cap was verified; its existence must not be inferred from working credentials or a configured model.

The probe built and completed successfully in Release. Its first invocation encountered a macOS `/var` versus `/private/var` project-reference resolution error; rerunning from the canonical physical temporary path succeeded without a source change. Only probe source/build artifacts were stored in the temporary directory. Configuration values remained in memory. Public KeyVault reads may append their normal access audit entries; no planning sessions, review drafts, campaign reservations or receipts were created or edited.

## Execution and accounting

| Requested evidence | Observed result |
| --- | --- |
| Native Desktop rendering and chat | Not exercised; stopped before launch and prompt submission. |
| Selected agent and real `workflow.plan` trace | Not exercised. The repository's unchanged generic planning bootstrap remains available. |
| MCP-authenticated GitHub identity and publication permission | Not verified. CLI authentication is not evidence of the MCP identity. |
| Tested PR URLs, base/head SHAs | None; no PR was promoted into an execution. |
| Planning/execution identities | None created for this validation. |
| Planner calls / repairs | 0 / 0. |
| Copilot inference calls | 0. |
| Additional inference tokens / cost | 0 / EUR 0 because nothing was dispatched, not because usage was missing. Prior known and uncertain charges are unchanged. |
| Intent, graph, YAML, scenarios and approved artifact | None generated. |
| Real MCP operations and command evidence | None; no Git/Cmd/Copilot workflow tools invoked. |
| Dependency installation, build, lint, unit/integration checks on SmartGuide | Not run. |
| Review execution duration | Not applicable; no execution started. |
| Dedicated review workspace and cleanup | No review workspace or clone created; no emergency cleanup required. |
| Immutable review, confirmation, publication URL/ID and exactly-once verification | Not applicable; no draft or GitHub write. |
| Generic fixes / regressions | None; no production defect was patched. |

There were no SmartGuide source changes, commits, pushes, PR metadata changes, reviews, merges, deployments or unrelated repository writes. Existing live sessions and benchmark evidence were not resumed or replaced. Only this sanitized GnOuGo report is delivered.

Delivery checks: the Release configuration probe passed and all eight local document links resolve. The report is checked with `git diff --check`. No production source or tests changed, so the complete release suite was not rerun; its previous results remain in the separate finalization report.

At `2026-09-20T10:13:07Z`, [PR #99](https://github.com/GnouGo/GnouGo/pull/99) checks on `5832552` had no reported failures: completed checks passed, package publication jobs were skipped and six Desktop platform jobs were still running. One human approval was still required. This snapshot does not claim those pending checks passed, nor does it establish real Desktop readiness.

## Required condition to resume

Provide verifiable existing host/provider enforcement of a **new, isolated EUR 20 aggregate allowance covering both planner and Copilot inference**, including uncertain attempts and retries, with durable accounting across restarts. A configured proxy URL, an after-the-fact usage report or a planner-only budget is insufficient evidence. This validation does not implement a new proxy or relax the ceiling.

Once that prerequisite is satisfied, resume at Desktop setup and integration authentication, dynamically discover suitable PRs and perform the first complete read-only review. Native UI interaction, separate workflow approval and publication confirmation, fresh execution for publication, head-SHA verification, real command observations and cleanup all remain untested requirements. Success still requires three complete read-only reviews and the additional freshly executed, explicitly confirmed publication.
