# Planning evaluation corpus

Eight frozen requests cover arithmetic, read/transform, nullable values/defaults, routing, parallel collections/subflows, protected writes/cleanup, the original French PR review and an English review with 80 irrelevant tools. All external integrations are mocked. Expected results use independent alternate inputs and observations; review evaluation checks passing, failed and incomplete executions in one clone. All eight offline intent fixtures exercise construction and execution. They are not a live-model reliability score. Rejected confirmation and changed-head cases must prevent publication while preserving cleanup.

```bash
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --live-command /absolute/path/to/model-adapter --campaign <fresh-candidate-id> --phase pilot
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --keyvault-provider openai --model <configured-model> --campaign <fresh-candidate-id> --phase pilot
```

A live adapter reads one source-generated `LLMRequest` JSON from stdin and returns one `LLMResponse` JSON on stdout. It must honor strict schemas, medium reasoning, durable request identities and token ceilings; enforce the aggregate evaluation budget; and return verified usage plus `usage.benchmark_cost_eur`. Configure credentials through the adapter's trusted configuration boundary. Each invocation is bounded to ten minutes. Missing usage is explicitly reported. It stops the campaign unless the HTTP journal provides a conservative bound admitted under the remaining budget. Never redispatch an uncertain request identity; command adapters must provide their own durable accounting and do not inherit the built-in HTTP recovery.

The built-in KeyVault option uses the Agent host's configuration mapper without starting the host or connecting to MCP integrations. Keep the same campaign ID for both revisions: its encrypted request receipts and EUR 50 ledger survive process restart. An OS lease serializes campaign dispatch, and a conservative per-request cost bound prevents spending beyond the remaining allowance. The provider HTTP layer may recover one uncertain generation attempt; its full possible usage is reserved before the new identity is dispatched. Exhaustion stops the campaign. Existing planning sessions are never read or modified.

JSONL output retains failures and reports first-pass validity, FinalReview, independent execution correctness, calls, repairs, verified input/output tokens, cost, initial request bytes (prompt plus response schema), estimated input tokens, scenarios and duration. The summary reports rates and cohort median/p75 calls. Nonzero exit means coverage or a cohort gate failed. `--case <name>` selects one frozen case. No generated YAML or model response is printed by the runner.

Fixture rows leave token usage and cost unknown (`null`). Live rows report partial known cost separately when a missing receipt makes total usage unknown. Initial request measurements include the strict response schema. The [accepted reliability report](../../docs/planning-default-response-domains-2026-09-20.md) records the completed pilot and measured cohort. The [migration report](../../docs/planning-business-intent-validation-2026-09-19.md) retains earlier request-size comparisons and incomplete evaluation evidence.

## Independent candidate campaign

The architecture remains frozen from `1f15bec`, with targeted corrections through the accepted behavior revision `65dc34a`. Its 7/7 pilot and measured gates passed; do not seek replacement samples to erase its retained failure. The runner remains available for future regressions. The runner defaults live evaluation to the seven candidate cases (all except `nullable_defaults`). `--cases` accepts a comma-separated selection without changing the frozen requests. Live evaluation requires a clean committed source tree.

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <fresh-candidate-id> --phase pilot
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <same-candidate-id> --phase measured
```

Pilot runs each case once. Measured evaluation requires the same revision's seven passing pilot results and runs three additional repetitions per case. Provider/model/request policy are resolved from KeyVault once at startup and pinned in the campaign's encrypted configuration. `--model` optionally asserts the expected configured model. One EUR 50 ceiling covers the entire new campaign, including failed runs and fixes; previous ledgers are untouched.

The existing encrypted request/receipt journal now also stores session checkpoints, usage receipts keyed by request identity, intermediate diagnostics and final run results. Repeating a command reuses completed results or resumes the reserved session. An uncertain attempt is never resent under its original identity. The shared HTTP retry policy may admit one new attempt after conservative accounting; exhausted or unjournaled uncertainty stops the campaign. `--inspect-run <source-sha>:<phase>:<case>:<repetition>` reads encrypted evidence to stdout for local diagnosis; add `--include-receipts` to inspect the original reserved schemas and responses; do not redirect private evidence to plaintext files or commit it.

Rows include source/session identity, phase, per-variant execution outcomes, confirmation/cancellation checks, safety violations and provisional diagnostic categories. Classification codes are an initial aid, not proof: inspect exact evidence and the independent oracle before confirming a cause or editing production code. Keep semantic misunderstanding, retrieval miss, invalid intent, builder defect, inference limitation, validator/oracle defect and provider/transport failure distinct. Safely rejected proposals are not executed safety violations.

Cancellation variants interrupt the mocked work operation before it returns a response,
then require a cancelled result, cleanup and no publication. Cancelling only after the last
operation completed can race with successful completion and is not a reliable interruption test.

Summary gates use all 21 measured runs on one revision: at least 19 reach FinalReview, at least 16 reach it within two calls, median calls at most two, zero safety violations and every approved artifact passing independent execution. Missing cases, mixed revisions or execution failures cannot pass. Pilot and measured statistics remain separate. Unknown usage is never zero; known partial tokens/cost are reported separately. Costs are metadata/FX estimates from provider usage, not invoices.

The historical [independent candidate report](../../docs/planning-candidate-reliability-2026-09-19.md) records a failed initial pilot, targeted nested-input inference correction and subsequent provider stop. That campaign did not establish measured reliability. The later accepted cohort is documented separately; campaigns, revisions and costs must not be pooled into a success rate.

## Offline replay of recorded interpretation

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --campaign <existing-campaign-id> --replay-run <source-sha>:pilot:<case>:1
```

Replay reads the original interpretation reservation and receipt through encrypted KeyVault records, validates against the original response schema and rebuilds with the current planner. It uses an in-memory session limited to that one receipt, runs the existing independent execution variants when compilation succeeds, and reports `mode: replay` with `live_model_calls: 0`. It never initializes a provider, writes campaign records, retries a missing receipt or counts toward live cohort statistics. Diagnostics that need another model decision remain unresolved. Exit 1 means the recorded proposal did not pass construction or independent execution. Inspection and replay work without provider configuration and may inspect a dirty working tree; record the tested revision when publishing results.

## Inspecting an uncertain request

`--campaign <id> --inspect-campaign` reports reservation/receipt counts, pending identities, the known budget snapshot and a hash of the campaign evidence. It is read-only and does not initialize model configuration. `--inspect-run <key> --include-receipts` now includes pending reservations even when no usage receipt exists, along with any retained safe failure metadata. Private request/receipt inspection still must not be redirected to plaintext files.

New failed dispatches retain the existing provider-neutral failure kind, HTTP status, safe provider code, retry metadata and failure stage in encrypted records. Exception messages, raw bodies and credentials are excluded. This does not authorize retries, create a completion receipt or retroactively recover missing metadata. A reservation remains uncertain if completion or receipt persistence fails. Missing-receipt replay exits 2 with `REPLAY_UNAVAILABLE`; no request is sent.

## Bounded HTTP recovery

The built-in KeyVault model now uses AI.Core's single HTTP retry loop. Provider `RetryPolicy`
configures total attempts, per-attempt timeout and the uncertain allowance (one by default).
The policy is pinned separately in the campaign. Generation remains synchronous and without
tools; workflow integrations remain mocked. No planner retry phase is introduced.
The adapter selects synchronous generation on its dispatch copy even when the planner
prefers background generation. The original reserved request and response schema stay intact.

Before each HTTP send, an encrypted `planning-evaluation-http-attempts` record stores its fresh
identity and a conservative token/cost allowance. Admission includes verified previous usage,
legacy campaign accounting and every unresolved allowance, with EUR 50 and eight physical
attempts per session. Uncertain original attempts and their allowances remain after recovery.
Known HTTP rejections do not count as generated usage. Calls count every reserved physical
attempt, including transient HTTP responses; repairs still count intent corrections only.

A stored complete HTTP response can be parsed and receipted after restart without another
send. Missing completion consumes the uncertain allowance and can only use a new identity.
The original model request/schema and all previous evidence stay unchanged. If a process stops
between original reservation and dispatch, the prepared journal permits safe restart. Old
reservations without HTTP evidence remain stopped; no migration invents usage or authorizes a
blind resend. Exhaustion and cancellation never replenish attempts. The OS campaign lease
serializes admission and record replacement.

Reports leave total usage/cost null after uncertainty, retain known partial usage, and add
`reserved_input_tokens`, `reserved_output_tokens`, `reserved_cost_eur` and `usage_bounded`.
A bounded unknown allows subsequent runs; it does not become verified usage. Replaying a
receipt replaces its measurement entry rather than adding another charge. Failed-run reports
are retained in `previous_results` if the same revision resumes its journaled pending request.
No paid evaluation or previous campaign modification is required to test this behavior.

Configure the built-in model through the provider secret's optional `retryPolicy` object,
for example `{"maxAttempts":4,"maxUncertainRetries":1,"attemptTimeoutMilliseconds":600000}`.
The object replaces the base retry policy; unspecified fields use the documented AI.Core
defaults. Invalid fields, ambiguous names and invalid limits fail configuration validation.
`Retry-After` is always honored; it cannot be disabled. Campaign summaries include
`campaign_accounting` with cumulative verified usage and conservative unknown allowances.

The [live HTTP recovery report](../../docs/planning-http-live-validation-2026-09-19.md)
records a recovered HTTP 500, unchanged accounting after restart, and seven FinalReview
results. One independently detected cleanup failure prevented the measured cohort.

The subsequent [cleanup validation report](../../docs/planning-cleanup-validation-2026-09-19.md)
records corrected cleanup ordering, a 21-run measured cohort and the cancellation-injection
correction. Its latest pilot is blocked by a separate input-default hole defect; results from
different revisions are kept separate and no overall reliability pass is claimed.

The [input-default validation report](../../docs/planning-input-default-validation-2026-09-19.md)
records literal-only default resolution, exact declaration repairs and atomic choice application.
The latest pilot reached 6/7 FinalReview with every reviewed artifact passing independent execution.
One model proposal exhausted its repairs, so measured evaluation remained gated. A recovered
uncertain transport attempt retains its conservative allowance; restart added no dispatch or charge.
