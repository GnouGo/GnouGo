# GnOuGo.Flow.Cli

The CLI validates and executes Flow workflows, with the typed planner injected for
`workflow.plan`. Configure a model and MCP capabilities in `appsettings.json` or
configuration overrides. Console prompts collect typed clarification and final artifact approval. YAML is produced by deterministic lowering
of the semantic tasks, selected choices and compiled graph. Planning storage uses format 10; execution journals remain schema 9. See [planning architecture](../../docs/workflow-planning-v9.md).

```sh
dotnet build src/GnOuGo.Flow.Cli
dotnet run --project src/GnOuGo.Flow.Cli -- validate src/GnOuGo.Flow.Cli/examples/workflow-planning.yaml
dotnet run --project src/GnOuGo.Flow.Cli -- run src/GnOuGo.Flow.Cli/examples/workflow-planning.yaml -i "task=Return a greeting"
dotnet publish src/GnOuGo.Flow.Cli -c Release -r osx-arm64
```

New `workflow.plan` sessions use compact construction version 2 with 64 cumulative
model calls and four corrections. Complete-request input limits are 3,000 for
intent indexing/selection/discovery, 8,000 for generation/correction and 4,000 for
Judges; tighter configured limits prevail, with no fallback to 32,000. Captured
output reservations remain 8,192 for non-Judge construction and 2,048 for Judges.
The general generation ceiling and
cumulative allowances can be configured at creation through the authored `generator.max_input_tokens`, `generator.max_output_tokens`,
`max_replan_attempts` and `llm_budget.max_calls` fields. The internal runtime does not
impose an additional eight-call cap. Financial, cumulative-token and time limits remain
unchanged. Saved definitions and missing historical fields retain their original limits;
resume rejects explicit changes instead of applying new creation defaults.

Fresh sessions require local and applicable intermediate advisory reviews, sharing
the same model, call allowance, usage journal and recovery. Historical sessions
retain their captured optional `business_judge` behavior; that option cannot disable
scoped reviews or add a final whole-plan Judge to a new session. Findings cannot authorize new effects or edits; observations
without existing explicit revision authority require revision. Console review includes
the retained request, requirements, validation results, plan and YAML; acknowledgments
and artifact approval remain explicit.

Before new planning and extraction dispatches, declared model capabilities must support
Structured Output, configured reasoning and the output reservation. The complete request
must fit the smaller configured/model input allowance. Missing indispensable metadata
fails before dispatch, without model substitution, silent clamping or evidence truncation.
See [planner configuration and review](../../docs/incremental-taskplan-construction.md#review-correction-and-finalization).

Runtime expression and WFScript support uses the existing Jint sandbox. Published
binaries retain the repository's documented, dependency-specific AOT exceptions.

Planning sessions persist through the encrypted KeyVault record API. The CLI prints a run ID;
use that identity with the revision-checked resume command after restart.
Runs now use the schema-9 encrypted execution journal. Inspect the current revision before resuming the same workflow and run identity:

```bash
gnougo-flow runs --tenant default --id RUN_ID
gnougo-flow run workflow.yaml --run-id RUN_ID --resume-revision REVISION
gnougo-flow runs --tenant default --id RUN_ID --command cancel --revision REVISION
```

Completed receipts are reused. Unknown external outcomes require reconciliation through the run API or CLI before execution or cleanup can continue. The CLI reports recovery status and the remaining step ceiling. Reusing a run ID without `--resume-revision` is rejected.
Completed model receipts are reused and an unverifiable dispatch stops without another call.

Configure bounded Copilot explicitly under `Flow:CopilotRunners`, mapping a runner
identifier to its configured MCP server name (for example `coding` to
`GnOuGo.GithubCopilot.Mcp`). Ordinary MCP execution needs no agent adapter.
See [adapter scope and evidence](../GnOuGo.Flow.Copilot/README.md).

```sh
gnougo-flow runs --tenant default --id RUN_ID --command reconcile --revision REVISION --invocation INVOCATION_ID
# For effects without a reconciling adapter, explicitly confirm quiescence; this records failure:
gnougo-flow runs --tenant default --id RUN_ID --command reconcile --revision REVISION --invocation INVOCATION_ID --confirmed-stopped-reason "Confirmed the external process exited; its result is unavailable"
```

Persistence paths can be configured with `KeyVault:DatabasePath`, `Flow:Execution:IndexPath`, `Flow:Execution:OwnerPath` and `Flow:Planning:OwnerPath`. Omitted paths use workspace helpers. Hosts sharing a KeyVault must share the execution owner directory.
