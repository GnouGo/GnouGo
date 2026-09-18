# Workflow planning

`PlanningSession → WorkflowIntentPlan → PlanningGraph → Diagnostics → Approval`

Natural-language interpretation is probabilistic. Construction, validation, compilation and execution authorization are deterministic. There is one planning path and no legacy compatibility mode.

## Reading the implementation

Start with `TypedWorkflowPlanner.cs`, `PlanningGraphBuilder.cs` and `PlanningGraphCompiler.cs` in `src/GnOuGo.Flow.Planning`. Core owns the provider-neutral session, intent, graph and runtime interfaces. Planning depends only on Core. Integrations and hosts supply models, capability discovery transports and persistence.

1. Discover the entire allowed capability catalog without a model call. Retain authoritative schemas, executable identities, effects and artifact contracts. An oversized interpretation request fails with an explicit input-budget diagnostic.
2. Ask for one strict `WorkflowIntentPlan`: workflows, typed ports, native steps or capability references, arguments, bindings, dependencies and control flow. The response cannot replace host policy or catalog contracts. A complete local workflow reaches final review with one call.
3. Build the graph deterministically. Resolve exact references, preserve omitted optional values versus explicit null, order dependencies, and expose missing fields as typed holes. The graph is executable authority; assignments change it without rewriting the intent.
4. Recompute finite hole domains from schemas, scope, availability and artifact contracts. Zero choices produce a located diagnostic; one resolves automatically; multiple choices use a bounded batch of issued IDs. Reject unissued IDs. Recompute domains after assignments. Missing computations and schemas use repair; missing business facts can use typed clarification. Runtime input declarations require no planning-time answer.
5. Validate types, literal values, dataflow, availability, dependencies, cycles, expressions, capability targets, artifact provenance and explicit host policy. Unknown external effects require confirmation by default. A host-owned entrypoint gate encloses the body, subworkflows and finalizers; rejection, abandonment or unavailable confirmation prevents entry.
6. Compile a valid, hole-free graph to YAML. Validate it using the runtime compiler and contract validator, then execute isolated scenarios for normal paths, branches, loops, failures, cancellation, cleanup and confirmation denial. Schemas/defaults/validated examples supply samples. Optional typed intent fixtures provide sample inputs and observation sequences when needed. Simulations do not establish live external correctness.
7. Repair with exact diagnostics and the same intent response shape. Repairs may replace the entire plan. Rebuild and revalidate completely. Defaults are two repairs per user-submitted intent and eight total model calls. An unchanged candidate with unchanged failures stops early.
8. Present intent, graph, YAML, findings and scenarios for final review. Approval binds the reviewed content and authoritative contracts. Any executable/content change invalidates it. Runtime write confirmation is separate from this approval.

## Public boundaries and failure behavior

`IWorkflowPlanner.AdvanceAsync(PlanningSession, PlanningCommand, IPlanningRuntime, CancellationToken)` advances to the next durable checkpoint. Commands are `advance`, `answer`, `revise`, `edit_intent`, `configure_generation`, `approve` and `cancel`. Agent.Server adds `save` and explicit `retry_model`. Commands require the expected revision; approval/save require the current artifact hash. Explicit revisions reset the repair allowance and retain cumulative calls, tokens, cost and active time.

`IPlanningRuntime` supplies discovery, model calls, executable/scenario validation, current-contract verification and checkpoints. `IPlanningRuntimeFactory.ReadApprovedYamlAsync` retrieves the trusted artifact by execution tenant, session and hash. `workflow.execute` verifies stored approval and rejects substituted prior-step YAML. Saving also verifies approval and current contracts. Existing workflows enter through graph import as baseline context and require complete validation and fresh approval.

The model never returns YAML, permissions or transport targets. Names/descriptions cannot grant permission. There are no source-span ownership, semantic proof, behavior approval, semantic-review model or patch phases.

## Budgets and restart

One configured reasoning level (`medium` by default), 12,000 input tokens and 8,192 output tokens per request; eight session calls and two repairs per submitted intent. Hosts retain token, monetary and active-time limits. No recursive decision pages or automatic output escalation. Human waiting time is recorded separately.

Schema 6 uses fresh encrypted `*-v6` collections and Agent.Server's `.GnOuGo/data/gnougo-planning-v6.db`. Old formats are rejected without migration; existing user databases are untouched. Agent.Server retains EF Core/SQLite indexes and compiled models; payloads and receipts use only public KeyVault record APIs. All keys are tenant-scoped. Durable reservations precede dispatch. A completed receipt replays without another charge; an uncertain dispatch stops without redispatch. Restart replenishes no allowance. Saving reconciles a previously committed identical artifact before writing again.

An operator can explicitly request `retry_model` for a stopped pending request. An available completion receipt is replayed. Otherwise Agent.Server retains the old request and call charge, accounts conservatively for unreported usage, and reserves a fresh request identity with the same prompt and limits. The estimate uses at least the configured input allowance or serialized request byte count, whichever is larger, and the entire enforced output allowance. It is budget accounting, not a provider usage receipt. The absolute correction is journaled before updating the cumulative ledger, so recovery after a crash does not charge it twice. Exhausted call, repair, token, or cost limits prevent another dispatch. Automatic restart never invokes this command.

## Validation

```bash
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet test tests/GnOuGo.Flow.Integrations.Tests
dotnet test tests/GnOuGo.Agent.Server.Tests
dotnet test GnOuGo.Agent.sln
dotnet build GnOuGo.Agent.sln -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64
dotnet run --project src/GnOuGo.Agent.Server -- --planning-persistence-smoke /tmp/gnougo-planning-smoke
```

The corpus checks local computation, reading/transformation and protected writes with cleanup against independent expected outputs and effects. It reports calls, repairs and scenarios. See its README for live-model evaluation with all external effects mocked. Published smoke programs exercise source-generated session serialization, native execution and encrypted EF persistence.

### Refactor verification — 2026-09-18

Verified locally with .NET SDK 10.0.300 on macOS arm64:

- Solution tests: 2,355 passed, zero failed, one environment-gated live Copilot test skipped.
- Solution build with `-warnaserror -p:SkipClientBuild=true`: zero warnings and errors. Both Agent.Server and Flow.Server frontend builds passed separately.
- Release packages: `GnOuGo.Flow.Core`, `GnOuGo.Flow.Planning`, and `GnOuGo.Flow.Integrations`.
- Published Native AOT planner smoke: all three corpus cases passed, each with one interpretation call and zero repairs.
- Published trimmed, self-contained Agent.Server persistence smoke: passed schema-6 encrypted storage, tenant isolation, and revision checks. Publication used `SkipClientBuild`, `SkipBundledServerTools`, and `SkipPlaywrightBrowserInstall`; bundled external tools were outside this persistence check.

The offline corpus uses deterministic model fixtures and mocked external effects. The live-model adapter path is available but was not exercised in this verification.

The subsequent [Agent.Server live validation report](planning-live-validation-2026-09-18.md) records the exact-prompt attempt, bounded failure, resulting fixes, and remaining execution/approval work.
