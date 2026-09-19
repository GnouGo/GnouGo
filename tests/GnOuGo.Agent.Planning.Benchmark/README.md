# Planning evaluation corpus

Eight frozen requests cover arithmetic, read/transform, nullable values/defaults, routing, parallel collections/subflows, protected writes/cleanup, the original French PR review and an English review with 80 irrelevant tools. All external integrations are mocked. Expected results use independent alternate inputs and observations; review evaluation checks passing, failed and incomplete executions in one clone. The three small offline fixtures remain a smoke check, not a live-model reliability score.

```bash
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --live-command /absolute/path/to/model-adapter --repetitions 3
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --keyvault-provider openai --model <configured-model> --campaign <shared-baseline-and-candidate-id> --repetitions 3
```

A live adapter reads one source-generated `LLMRequest` JSON from stdin and returns one `LLMResponse` JSON on stdout. It must honor strict schemas, medium reasoning, durable request identities and token ceilings; enforce the aggregate evaluation budget; and return verified usage plus `usage.benchmark_cost_eur`. Configure credentials through the adapter's trusted configuration boundary. Each invocation is bounded to ten minutes. Missing usage stops the campaign and is explicitly reported. Never automatically redispatch uncertain requests.

The built-in KeyVault option uses the Agent host's configuration mapper without starting the host or connecting to MCP integrations. Keep the same campaign ID for both revisions: its encrypted request receipts and EUR 50 ledger survive process restart. An OS lease serializes campaign dispatch, and a conservative per-request cost bound prevents spending beyond the remaining allowance. Provider failures without receipts stop the campaign. Existing planning sessions are never read or modified.

JSONL output retains failures and reports first-pass validity, FinalReview, independent execution correctness, calls, repairs, verified input/output tokens, cost, initial request bytes (prompt plus response schema), estimated input tokens, scenarios and duration. The summary reports rates and complex-case median/p75 calls. Nonzero exit means at least one independent execution expectation failed. `--case <name>` selects one frozen case. No generated YAML or model response is printed by the runner.
