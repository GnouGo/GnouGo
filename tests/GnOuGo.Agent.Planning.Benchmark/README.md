# Planning evaluation corpus

Eight frozen requests cover arithmetic, read/transform, nullable values/defaults, routing, parallel collections/subflows, protected writes/cleanup, the original French PR review and an English review with 80 irrelevant tools. All external integrations are mocked. Expected results use independent alternate inputs and observations; review evaluation checks passing, failed and incomplete executions in one clone. All eight offline intent fixtures exercise construction and execution. They are not a live-model reliability score. Rejected confirmation and changed-head cases must prevent publication while preserving cleanup.

```bash
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --live-command /absolute/path/to/model-adapter --repetitions 3
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --keyvault-provider openai --model <configured-model> --campaign <shared-baseline-and-candidate-id> --repetitions 3
```

A live adapter reads one source-generated `LLMRequest` JSON from stdin and returns one `LLMResponse` JSON on stdout. It must honor strict schemas, medium reasoning, durable request identities and token ceilings; enforce the aggregate evaluation budget; and return verified usage plus `usage.benchmark_cost_eur`. Configure credentials through the adapter's trusted configuration boundary. Each invocation is bounded to ten minutes. Missing usage stops the campaign and is explicitly reported. Never automatically redispatch uncertain requests.

The built-in KeyVault option uses the Agent host's configuration mapper without starting the host or connecting to MCP integrations. Keep the same campaign ID for both revisions: its encrypted request receipts and EUR 50 ledger survive process restart. An OS lease serializes campaign dispatch, and a conservative per-request cost bound prevents spending beyond the remaining allowance. Provider failures without receipts stop the campaign. Existing planning sessions are never read or modified.

JSONL output retains failures and reports first-pass validity, FinalReview, independent execution correctness, calls, repairs, verified input/output tokens, cost, initial request bytes (prompt plus response schema), estimated input tokens, scenarios and duration. The summary reports rates and complex-case median/p75 calls. Nonzero exit means at least one independent execution expectation failed. `--case <name>` selects one frozen case. No generated YAML or model response is printed by the runner.

Fixture rows leave token usage and cost unknown (`null`). Live rows report partial known cost separately when a missing receipt makes total usage unknown. Initial request measurements include the strict response schema. The [validation report](../../docs/planning-business-intent-validation-2026-09-19.md) distinguishes static request-size comparisons, fixture execution and incomplete live evaluation.

## Independent candidate campaign

Freeze the planner at `1f15bec` unless a classified live failure has a generic, reproducible cause. The runner defaults live evaluation to the seven candidate cases (all except `nullable_defaults`). `--cases` accepts a comma-separated selection without changing the frozen requests. Live evaluation requires a clean committed source tree.

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <fresh-candidate-id> --phase pilot
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <same-candidate-id> --phase measured
```

Pilot runs each case once. Measured evaluation requires the same revision's seven passing pilot results and runs three additional repetitions per case. Provider/model/request policy are resolved from KeyVault once at startup and pinned in the campaign's encrypted configuration. `--model` optionally asserts the expected configured model. One EUR 50 ceiling covers the entire new campaign, including failed runs and fixes; previous ledgers are untouched.

The existing encrypted request/receipt journal now also stores session checkpoints, usage receipts keyed by request identity, intermediate diagnostics and final run results. Repeating a command reuses completed results or resumes the reserved session. An uncertain dispatch stops this campaign without resending. `--inspect-run <source-sha>:<phase>:<case>:<repetition>` reads encrypted evidence to stdout for local diagnosis; do not redirect private evidence to plaintext files or commit it.

Rows include source/session identity, phase, per-variant execution outcomes, confirmation/cancellation checks, safety violations and provisional diagnostic categories. Classification codes are an initial aid, not proof: inspect exact evidence and the independent oracle before confirming a cause or editing production code. Keep semantic misunderstanding, retrieval miss, invalid intent, builder defect, inference limitation, validator/oracle defect and provider/transport failure distinct. Safely rejected proposals are not executed safety violations.

Summary gates use all 21 measured runs on one revision: at least 19 reach FinalReview, at least 16 reach it within two calls, median calls at most two, zero safety violations and every approved artifact passing independent execution. Missing cases, mixed revisions or execution failures cannot pass. Pilot and measured statistics remain separate. Unknown usage is never zero; known partial tokens/cost are reported separately. Costs are metadata/FX estimates from provider usage, not invoices.
