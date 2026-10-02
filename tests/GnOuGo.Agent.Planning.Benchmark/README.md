# Planning evaluation corpus

## Planner stabilization comparison

The [completed campaign report](../../docs/evidence/planner-stabilization/results-7d5d42b2.md)
records the frozen final production revision, passing comparison and retained failures.

`--stabilization` is the execution-based eleven-case campaign. It uses the eight
unchanged corpus requests and three real filesystem workflows through the shipped
Cmd MCP stdio process. Real effects are confined to a disposable workspace per run;
the oracle checks contents, unchanged inputs, permission denial, and creation/cleanup
calls independently. No remote publication is part of this campaign.

Build the benchmark and Cmd MCP in Release. Freeze the harness before collecting
the baseline, and use that same harness against both production revisions:

```sh
# Baseline worktree: 22bba1a5 (production 7288b699).
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release -m:1 -warnaserror -p:SkipClientBuild=true
dotnet build src/GnOuGo.Cmd.Mcp -c Release -m:1 -warnaserror
STABILIZATION_CMD_DIR="$(mktemp -d)"
cp -R src/GnOuGo.Cmd.Mcp/bin/Release/net10.0/. "$STABILIZATION_CMD_DIR/"
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --stabilization --campaign planner-stabilization-20260930 --cohort baseline \
  --source 7288b6997c704b8ca098761c030071d61da1ce1a \
  --cmd-executable "$STABILIZATION_CMD_DIR/GnOuGo.Cmd.Mcp"
# Switch to the frozen candidate worktree; keep STABILIZATION_CMD_DIR unchanged.
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release -m:1 -warnaserror -p:SkipClientBuild=true
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --stabilization --campaign planner-stabilization-20260930 --cohort final --source <candidate-sha> \
  --cmd-executable "$STABILIZATION_CMD_DIR/GnOuGo.Cmd.Mcp"
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --stabilization --campaign planner-stabilization-20260930 \
  --compare 7288b6997c704b8ca098761c030071d61da1ce1a --candidate <candidate-sha>
```

Run collection in clean isolated worktrees at the recorded harness/production
revisions: baseline harness `22bba1a5` over production `7288b699`, and the candidate
revision linked in the evidence report. Build each runner in its own worktree and
share only the frozen Cmd directory and encrypted campaign. The recorded Cmd binary
was built at `456b05c7` (Cmd source identical to the production baseline); its exact
assembly hash is retained in every manifest. Reuse that frozen binary for historical
collection/replay. A separately authorized fresh campaign must freeze its own binary
once and use that identical directory for both revisions. The manifest hashes
must match before candidate dispatch. Existing retained identities are read-only
replays; a new measurement campaign needs its own explicitly authorized allowance.

`--source` permits a harness-only commit over that production tree, never changed
production. `--cmd-executable` supplies an absolute path to a separately built server.
Copy the complete Cmd Release directory outside subsequent build outputs before
starting collection, and pass that frozen apphost with `--cmd-executable` to both
cohorts. A rebuild can change assembly hashes through source-version metadata even
when Cmd source is unchanged.
The campaign pins harness/oracle/accounting/configuration hashes, the Cmd assembly,
OS/runtime and limits. Each row records production and harness commits separately.
Both cohorts require three repetitions of all eleven cases. `--cohort diagnostic
--cases <names>` records one separate diagnostic repetition per source revision.
Reissuing a completed identity reads its result without another dispatch. Interrupted
execution is failed evidence; it is never automatically repeated. Interrupted planning
uses the existing encrypted request journal and original schemas.

The fresh EUR 50 ceiling covers both revisions, diagnostics and execution inference.
Logical planning calls, physical attempts, discovery reads, repairs, tokens and
planning/execution/total latency are distinct. Unknown usage remains unknown. Final
acceptance requires 33 correct executions, successful nominal variants, zero safety
violations, and one planning call with zero repairs for each simple case. Incomplete
or mismatched cohorts are inconclusive. Historical review-based gates do not apply.

Read a sanitized result with `--stabilization --campaign <id> --inspect
<source>:<cohort>:<case>:<repetition>`. `--private-evidence` additionally returns the
encrypted session/diagnostic evidence for local inspection; never redirect that
private output to plaintext files or commit it. All failure identities and budget
reservations remain retained. Generic fixes and further diagnostic iterations are
authorized for this campaign within its shared ceiling; failed final cohorts are
not overwritten or pooled with a later revision.

## Historical corpus campaigns

Eight frozen requests cover arithmetic, read/transform, nullable values/defaults, routing, parallel collections/subflows, protected writes/cleanup, the original French PR review and an English review with 80 irrelevant tools. All external integrations are mocked. Expected results use independent alternate inputs and observations; review evaluation checks passing, failed and incomplete executions in one clone. All eight offline intent fixtures exercise construction and execution. They are not a live-model reliability score. Rejected confirmation and changed-head cases must prevent publication while preserving cleanup.

```bash
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --live-command /absolute/path/to/model-adapter --campaign <fresh-candidate-id> --phase pilot
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- --keyvault-provider openai --model <configured-model> --campaign <fresh-candidate-id> --phase pilot
```

A live adapter reads one source-generated `LLMRequest` JSON from stdin and returns one `LLMResponse` JSON on stdout. It must honor strict schemas, medium reasoning, durable request identities and token ceilings; enforce the aggregate evaluation budget; and return verified usage plus `usage.benchmark_cost_eur`. Configure credentials through the adapter's trusted configuration boundary. Each invocation is bounded to ten minutes. Missing usage is explicitly reported. It stops the campaign unless the HTTP journal provides a conservative bound admitted under the remaining budget. Never redispatch an uncertain request identity; command adapters must provide their own durable accounting and do not inherit the built-in HTTP recovery.

The built-in KeyVault option uses the Agent host's configuration mapper without starting the host or connecting to MCP integrations. Keep the same campaign ID for both revisions: its encrypted request receipts and EUR 50 ledger survive process restart. An OS lease serializes campaign dispatch, and a conservative per-request cost bound prevents spending beyond the remaining allowance. The provider HTTP layer may recover one uncertain generation attempt; its full possible usage is reserved before the new identity is dispatched. Exhaustion stops the campaign. Existing planning sessions are never read or modified.

JSONL output retains failures and reports first-pass validity, FinalReview, independent execution correctness, calls, repairs, verified input/output tokens, cost, initial request bytes (prompt plus response schema), estimated input tokens, static validation results and duration. The summary reports rates and cohort median/p75 calls. Nonzero exit means coverage or a cohort gate failed. `--case <name>` selects one frozen case. No generated YAML or model response is printed by the runner.

Fixture rows leave token usage and cost unknown (`null`). Live rows report partial known cost separately when a missing receipt makes total usage unknown. Initial request measurements include the strict response schema. The historical [accepted reliability report](../../docs/planning-default-response-domains-2026-09-20.md) records the completed pilot and measured cohort. The [migration report](../../docs/planning-business-intent-validation-2026-09-19.md) retains earlier request-size comparisons and incomplete evaluation evidence.

## Independent candidate campaign

Schema-9 replaces the former planning architecture. Historical reliability reports describe their recorded revisions and do not establish a pass for this implementation. The replacement campaign compares against parent `46c2c77` in an isolated checkout, with the same model and limits. The runner defaults live evaluation to all eight frozen cases, including `nullable_defaults`. `--cases` accepts a comma-separated selection without changing the frozen requests. Live evaluation requires a clean committed source tree.

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <fresh-candidate-id> --phase pilot
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider openai --campaign <same-candidate-id> --phase measured
```

Pilot runs each case once. Measured evaluation requires the same revision's eight passing pilot results and runs three additional repetitions per case. Provider/model/request policy are resolved from KeyVault once at startup and pinned in the campaign's encrypted configuration. `--model` optionally asserts the expected configured model. One EUR 50 ceiling covers the entire new campaign, including failed runs and fixes; previous ledgers are untouched.

The existing encrypted request/receipt journal now also stores session checkpoints, usage receipts keyed by request identity, intermediate diagnostics and final run results. Repeating a command reuses completed results or resumes the reserved session. An uncertain attempt is never resent under its original identity. The shared HTTP retry policy may admit one new attempt after conservative accounting; exhausted or unjournaled uncertainty stops the campaign. `--inspect-run <source-sha>:<phase>:<case>:<repetition>` reads encrypted evidence to stdout for local diagnosis; add `--include-receipts` to inspect the original reserved schemas and responses; do not redirect private evidence to plaintext files or commit it.

Rows include source/session identity, phase, per-variant execution outcomes, confirmation/cancellation checks, safety violations and provisional diagnostic categories. Classification codes are an initial aid, not proof: inspect exact evidence and the independent oracle before confirming a cause or editing production code. Keep semantic misunderstanding, retrieval miss, invalid intent, builder defect, inference limitation, validator/oracle defect and provider/transport failure distinct. Safely rejected proposals are not executed safety violations.

Cancellation variants interrupt the mocked work operation before it returns a response,
then require a cancelled result, cleanup and no publication. Cancelling only after the last
operation completed can race with successful completion and is not a reliable interruption test.

Summary gates use all 24 measured runs on one revision: at least 22 reach FinalReview, at least 19 reach it within two calls, median calls at most two, zero safety violations and every approved artifact passing independent execution. Missing cases, mixed revisions or execution failures cannot pass. Pilot and measured statistics remain separate. Unknown usage is never zero; known partial tokens/cost are reported separately. Costs are metadata/FX estimates from provider usage, not invoices.

The historical [independent candidate report](../../docs/planning-candidate-reliability-2026-09-19.md) records a failed initial pilot, targeted nested-input inference correction and subsequent provider stop. That campaign did not establish measured reliability. The later accepted cohort is documented separately; campaigns, revisions and costs must not be pooled into a success rate.

## Offline replay of the first recorded action

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --campaign <existing-campaign-id> --replay-run <source-sha>:pilot:<case>:1
```

Replay reads the first planning-action reservation and receipt through encrypted KeyVault records, validates against the original response schema and rebuilds with the current planner. It uses an in-memory session limited to that one receipt, runs the existing independent execution variants when compilation succeeds, and reports `mode: replay` with `live_model_calls: 0`. It never initializes a provider, writes campaign records, retries a missing receipt or counts toward live cohort statistics. A first action that only selects capabilities cannot reconstruct a later graph. Diagnostics that need another model decision remain unresolved. Exit 1 means the recorded proposal did not pass construction or independent execution. Inspection and replay work without provider configuration and may inspect a dirty working tree; record the tested revision when publishing results.

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
records a recovered HTTP 500, unchanged accounting after restart, and eight FinalReview
results. One independently detected cleanup failure prevented the measured cohort.

The subsequent [cleanup validation report](../../docs/planning-cleanup-validation-2026-09-19.md)
records corrected cleanup ordering, a 21-run measured cohort and the cancellation-injection
correction. Its final recorded pilot was blocked by a separate input-default hole defect;
that historical revision did not establish a reliability pass.

The [input-default validation report](../../docs/planning-input-default-validation-2026-09-19.md)
records literal-only default resolution, exact declaration repairs and atomic choice application.
That report's pilot reached 6/7 FinalReview with every reviewed artifact passing independent execution.
One model proposal exhausted its repairs, so measured evaluation remained gated. A recovered
uncertain transport attempt retains its conservative allowance; restart added no dispatch or charge.

## Exhausted, inconclusive runs

`--retain-inconclusive-run <commit:phase:case:repetition> --campaign <id>` closes an already failed evaluation only after its eight HTTP attempts are exhausted. It writes a separate encrypted audit record under the campaign lock. The original run, failure, request and HTTP evidence remain unchanged; no completion receipt is invented. Unknown attempts retain their full cost reservation in the same EUR 50 campaign ceiling. That request identity can never dispatch again, while different evaluation identities may use the remaining campaign allowance. `--inspect-campaign` reports both uncertainty and closures. This does not turn an inconclusive run into a successful measurement.

## Parent comparison

The read-only comparison loads all three repetitions for both revisions from the
existing encrypted campaign. It reports incomplete cohorts, changed models/limits
or unbounded usage as inconclusive. Passing requires no per-case correctness
regression, improvement on the retained complex failure, lower median calls and
no unsafe or incorrect approved execution. All failed runs remain in the cohort.
This report supplements the existing pilot and measured gates.

## Stabilization acceptance against two baselines

The authorized schema-9 stabilization uses one candidate cohort of eight cases and
three repetitions, after deterministic validation and CI. Collect it with the
existing `--phase fixture --repetitions 3` mode and verify that every row records
`mode: live`. Its relative acceptance uses the following read-only comparison;
the historical absolute pilot/measured thresholds are not additional gates for
this stabilization. The collection summary's exit code is not this comparison's
acceptance result.

```bash
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --campaign flow-v9-112 \
  --compare-parent 46c2c77fea19c952d3258d744d886fe52ef52d33 --parent-phase fixture \
  --compare-reference 87acc5f0e41702e193795b45b4ddfab1055d2143 --reference-phase fixture \
  --candidate <frozen-candidate-commit> --candidate-phase fixture \
  --retained-case review_french --audit-admission-denials
```

Both baseline cohorts must be complete, comparable and usage-bounded. Every case
must retain at least as many correct repetitions as each baseline. Median calls
must improve on the parent; the reference median is reported without an additional
gate. The retained French case must improve on the parent, and no approved
candidate execution may be incorrect or unsafe. All failed outcomes stay in the
denominator. The existing admission audit is read-only and never changes outcomes.

Use the same pinned model/limits and cumulative EUR 50 campaign, with no additional
paid pilots. Never rerun failed repetitions under replacement identities. If the
collection stops, retain its original evidence and continue only unattempted cohort
identities when journal safety and existing budget admission permit. Incomplete
coverage or unbounded usage is inconclusive. Stop implementation and paid evaluation
after this cohort, including when it fails.

```bash
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --campaign flow-v9-112 --compare-parent 46c2c77fea19c952d3258d744d886fe52ef52d33 \
  --parent-phase fixture --candidate <tested-candidate-sha> --retained-case review_french
```

The parent campaign used the `fixture` phase label to collect its failed baseline;
those receipts still must record live inference with the same model and limits.

For an ungated three-repetition live comparison after a failed pilot, use the existing
`--phase fixture --repetitions 3` collection mode with the same campaign and provider.
Read those results with `--compare-parent <sha> --candidate <sha> --candidate-phase fixture`
(and `--parent-phase fixture` when applicable). Comparison still requires 24 live results,
identical model and limits, no per-case correctness regression, retained complex-case
improvement and reduced median calls. This records the failed pilot and its unsuccessful
repairs; it does not pass or change the stricter pilot/measured acceptance gates.

A fixture cohort interrupted by a terminal per-session limit can continue at a
missing ordinal with `--phase fixture --first-repetition 3 --repetitions 3`.
Existing run keys, failures and reservations are retained. This does not reopen the
failed session or authorize another attempt for it. When only this runner entry
point or its README changed, `--evaluation-source <full-commit>` retains the frozen
source identity and records `harness_commit` separately. The command rejects any
other file difference, including production, frozen cases, oracles, measurements,
model transport and spending-accounting changes. Read-only comparison still loads
all three repetitions, including earlier failures and inconclusive runs.

### Auditing an admission denial

The original runner counted a logical reservation denied by the HTTP admission
limit as an extra call with unknown usage. New measurements record zero usage and
zero attempts when the durable journal proves no HTTP dispatch occurred. Actual
timeouts and other uncertain dispatches keep their conservative reservation.

For historical comparison, `--audit-admission-denials` is an explicit read-only
option. It requires a permanently closed session at its eight-attempt ceiling,
unchanged run/request hashes, an empty HTTP attempt journal, the recorded admission
failure, no completion receipt, and agreement between receipt counts and the
session ledger. It applies symmetrically to both cohorts. Ineligible evidence stays
unchanged. The report includes the raw comparison, original and audited result,
proof hashes and measurement commit. Only the detached usage and physical call
count change; the failed outcome remains failed. No request, receipt, original run,
closure or spending reservation is written, erased, refunded or redispatched.

## TaskPlan candidate: one bounded complex cohort

The format-10 replacement freezes production, operation metadata, harness and oracles
before evaluating only `conditional,review_french,review_distractors`, three repetitions
each, in the existing `flow-v9-112` campaign. It uses the pinned OpenAi provider,
`gpt-5.5-2026-04-24`, medium reasoning, 96,000 input and 32,768 output tokens, eight
physical attempts and two repairs. The cumulative EUR 50 ceiling includes all earlier
uncertain reservations. No pilots, replacement repetitions or budget reset are allowed.
A terminal journal or budget stop ends implementation and paid evaluation.

```sh
dotnet run --no-build -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --keyvault-provider OpenAi --model gpt-5.5-2026-04-24 --campaign flow-v9-112 \
  --phase fixture --cases conditional,review_french,review_distractors --repetitions 3
```

Compare using `--compare-parent`, `--compare-reference`, `--compare-stabilization`,
`--candidate`, the three `--cases`, and fixture phases. `CompareBest` requires complete
three-repetition evidence, matching model/limits, bounded usage, no unsafe or incorrect
approval, and no per-case correctness regression against the maximum retained baseline.
Calls, tokens and latency are separate metrics. Historical fixture/pilot gates are not
additional readiness criteria for this cohort.

`--audit-closed-http` optionally proves bounds for an exhausted, permanently closed
session using every durable HTTP journal, the unchanged closure/request hashes and the
campaign ledger. The report retains original results and proof hashes. Unknown usage
remains unknown and fully reserved; no receipt, success or measured token count is
invented. This read-only audit cannot dispatch, alter original encrypted evidence,
replenish an allowance or raise the campaign ceiling.

Execution journals and the real Copilot edit/test limitation remain unchanged. Mocked
workflow outcomes do not establish real sandboxed Copilot command execution.

## Authorized schema-portability live campaign

Replay a retained proposal against its original discovered contracts without inference,
execution, approval or mutation of the saved session:

```sh
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run recovery2-amazon-2
```

The [schema-portability report](../../docs/planning-schema-portability-2026-10-02.md)
retains the original provider rejection, diagnostic iterations and separate frozen
cohorts. This campaign performs paid inference and real external execution only
under the explicit authorization recorded for `schema-portability-20261002`.
It shares one encrypted EUR 50 ledger across planning, Flow inference and intercepted
Copilot SDK inference. Missing receipts retain conservative reservations.

Build a clean committed checkout before collection. Keep the same source and
configuration for every run within a cohort; a changed candidate requires a new
cohort identity under the same campaign. Do not repeat the original diagnostic.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability plan --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4 --case amazon --run final4-amazon-1
# Review the retained artifact first, then provide its exact printed hash:
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability execute --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4 --case amazon --run final4-amazon-1 \
  --artifact-hash <reviewed-artifact-hash>
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability report --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4
```

Use repetitions 1–3 for `amazon` and `code`, preserving every failed slot. Execution
requires a terminal with explicit human answers. Recommendations never approve a
call, and a started execution cannot be replayed. Browser debug `KeepBrowserOpen`
is disabled only in the spawned disposable process so cleanup can be checked.
Code feedback remains local; GitHub publication is excluded and untested.

`inspect-run --run <identity>` returns private retained requests, responses,
execution events and oracles; redirect only to an access-restricted local file.
`report` returns sanitized counters and rejects mismatched or missing manifests.
`discovery_reads` counts actual MCP `tools/list` calls. Planning/execution latency
includes human waiting; provider attempts, verified tokens, unknown reservations
and costs are separate. Costs are provider-usage/FX estimates, not invoices.
Absent and interrupted runs stay in the six-run denominator. Compilation or
FinalReview alone never passes the execution gate.

### Recovery and MCP Tasks follow-up

The recovery follow-up continues `schema-portability-20261002`; do not create a new
budget or overwrite final4. Use a new cohort (for example `recovery1`) and its six
`recovery1-{amazon,code}-{1,2,3}` identities after freezing the tested candidate.
The manifest also pins the execution PATH hash. Keep the disposable toolchain PATH
identical for planning and execution: Node 24.20.0, pnpm 10.34.5 and Python 3.11.13.
The repository manifests and recorded command results must independently confirm
those versions. Installing a toolchain does not grant a workflow new permissions.

Run the existing `--schema-portability readiness --case amazon` and `--case code`
commands first, using the same `--workspace` and campaign. Verify Browser closure,
a writable disposable workbook destination, exact SmartGuide head/base availability,
dependency installation and command permissions. Record missing services/toolchains
as explicit blockers. The [recovery report](../../docs/planning-recovery-and-live-blockers.md)
separates deterministic adapter execution from live-provider results.
