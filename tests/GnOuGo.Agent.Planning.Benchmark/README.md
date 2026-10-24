# Planning evaluation corpus

## First-proposal measurements

The first non-null submitted `plan` response and its diagnostics are captured separately
from discovery, clarification, correction and physical transport attempts. Rows report
`first_proposal_structurally_valid`, `first_proposal_business_correct`, FinalReview and
execution-oracle success separately. The historical `first_pass_valid` column is now an
alias for first-proposal structural validity; it is no longer a one-call metric.

Business correctness is assigned from an independent execution oracle only when the
executed request, requirements and plan match the unchanged first proposal. Observer
metadata and later approval-hash changes do not create a different candidate. Original proposals that were changed
before execution remain unknown unless independently assessed. Omitted requirements,
extra operations and redundant tasks remain null when the oracle has not established
those individual judgments. Unknown usage remains null; fixture tokens are estimates,
never provider usage. Failures and prior results are retained.

The retained live runtime campaign keeps its explicit 96,000-token input and
32,768-token output ceilings; these are separate from fresh planning defaults. An oversized full serialized request returns
`LLM_BUDGET_EXCEEDED`, `classification: request_input_allowance`,
`measurement: conservative_token_estimate`, the applied `limit` and measured
`current`, and `dispatch_status: not_started`. Output-limit refusals use
`request_output_allowance` and `declared_output_tokens`. These single-request
refusals are distinct from cumulative budget exhaustion. Neither the private
payload nor generated code enters diagnostics; failed-step context is supplied by
the existing engine. A local mapping attempt rejected before dispatch is not a
physical provider attempt or billed usage. Existing encrypted receipts and limits
remain unchanged during recovery.

Fresh external-runtime inference captures one HTTP attempt with no uncertain retries,
for both ordinary interpretation/mapping and the intercepted Copilot SDK. Every failed
attempt without verified usage, including a received 429, retains its full campaign
reservation. An exact saved completion may be recovered without sending; a reserved
physical attempt without a completed response and a recorded failed HTTP attempt stop
without resend. Historical policy and receipt bytes remain unchanged; historical SDK
records lacking the exact upstream identity cannot be recovered automatically. The
existing permanent-retention operator covers planning only, so runtime uncertainty
remains blocked pending separately authorized reconciliation. These runtime safeguards do not expand artifact approval,
MCP permissions or the existing runtime input, output, time and financial limits.

The [18 October Amazon retest](../../docs/evidence/bounded-observations-2026-10-18/README.md)
preserves a structurally valid first proposal that failed before extraction dispatch.
Its scalar extraction still bound the complete observation; zero extraction provider
calls does not mean zero runtime cost, because an earlier transform completed.
The subsequent generic prompt correction is separate from that frozen failed run.
It explains full learning inputs, deterministic projection and shared `each` context;
its presence alone is not live business-correctness evidence.

Live corpus and stabilization runs stop at FinalReview. Inspect the retained request,
requirements, tasks and uncertainty, then supply `--review-run <source:phase:case:repetition>`
and `--review-command <file>` with the existing explicit `approve` command (reviewer-set
`expectedRevision`, `artifactHash`, and `reviewedRequirementIds`). Stabilization uses
`source:cohort:case:repetition`. No live acknowledgments are synthesized. Scripted corpus
execution uses the independently authored fixture requirement IDs. An execution already
started is never repeated after uncertain completion.

Add `--first-proposal-gate` to the existing corpus or stabilization comparison command
for the separately authorized live gate: at least 25% fewer business-incorrect first
proposals, no safety regression and no higher median planning calls or total planning
tokens. Three repetitions, identical model/settings and frozen fixture/contract/oracle
material are required. Missing judgments, unknown totals and incomparable cohorts are
inconclusive. A zero-failure baseline must remain zero but cannot establish a relative
reduction. Descriptive Wilson intervals report sampling uncertainty. Historical gates
remain available for their original recorded cohorts.

The [implementation comparison](../../docs/evidence/first-attempt-taskplan-2026-10-17/README.md)
is offline. No paid inference or externally effectful live execution was authorized.

## Historical optional Judge and convergence measurements

The retained pre-construction corpus and stabilization profiles used twelve planning
calls, four corrections, 32,000 input tokens and 32,768 output tokens. In those
profiles, `--business-judge` enabled an optional whole-plan advisory review.
Fresh incremental sessions require scoped reviews and use the 8,192-token draft
and 2,048-token Judge reservations described below. The legacy Judge switch cannot
disable those reviews or select historical generation for a new session. Both runners
accept `--max-model-calls`, `--max-repairs`, `--max-input-tokens` and
`--max-output-tokens` for controlled comparisons. Retained runs use their persisted
settings; a new configuration requires a new frozen run identity, not mutation of old
evidence. Historical campaigns below keep their original explicit limits.
Omitted switches preserve retained values; explicit mismatches, including
`--no-business-judge` against an enabled run, fail before continuing it. A cohort's
settings are frozen before collection. The stabilization context manifest is version 2;
version 1 records remain immutable and inspectable, and a changed harness requires a
new campaign.

For retained historical comparisons, compare Judge enabled/disabled under identical budgets, model/settings, contracts,
cases and independent oracle versions. Measure changes to creation defaults separately;
a larger allowance and an extra observer are different interventions. Freeze the
complete candidate and fixture/contract/oracle hashes before collecting each comparable
cohort. Model evaluation still requires separate paid-call authorization and the existing
cumulative financial ceiling. This implementation does not itself establish live benefit
or authorize a new campaign.

The corpus runner reserves separate identities with `--arm judge` or `--arm defaults`;
the unqualified identity remains the control. Use `--business-judge --arm judge` for
the observer arm, or numeric overrides with `--arm defaults` for the budget arm.
`--compare-parent <same-commit> --candidate <same-commit> --judge-comparison`
loads control and Judge arms; `--defaults-comparison` loads control and defaults arms.
The existing parent/candidate phase switches still select the stored cohorts.
Stabilization uses its `baseline` and `final` cohort identities with the same commit,
then accepts the same comparison switches alongside `--compare` and `--candidate`.
Both reports require three repetitions, a common source/context/model and independent
outcomes. A Judge comparison rejects changed budgets; a defaults comparison requires
the Judge disabled in both arms. Missing settings, labels or incompatible arms remain
inconclusive. These descriptive reports do not declare the first-proposal gate passed
or establish a live benefit. Unknown costs and tokens have null medians.

`candidate_reviews` retains each compiled candidate separately from `first_proposal`.
Independent execution labels apply only to the matching request, revision, answers,
requirements, discovery, contracts and plan hash
once all expected oracle variants have run. A changed or unexecuted first proposal stays
unknown. `business_review` reports independently labelled candidate-level true/false
positives and negatives, unknown or unjudged candidates, convergence and introduced
regressions. These are artifact-level measurements, not per-finding precision. A
syntactically valid Judge report is never an oracle label; a no-findings report can be a
false negative, and a finding can be a false positive. Invalid, skipped and inconclusive
reviews remain unjudged.

`planning_stages` separates logical calls and elapsed time by the saved call purpose,
including `judge`, and aggregates usage/cost from the corresponding receipts. Physical
attempts require transport evidence; absent counts, usage and costs remain null.
Correction counts and final stop outcomes remain separate from first-proposal validity.
Judge calls use the same reservations, receipts and cumulative allowances as generation
and correction; there is no parallel allocation. Completed reviews are reused for an
unchanged context, including recovery.

Offline adapters return explicitly scripted reports. Fixtures, negative variants and
replay test accounting, scope protection and oracle classification; they cannot measure
a real model's false-positive rate or prove business improvement. Preserve failures and
inconclusive candidates. Keep live-model, real-execution and scripted results separately
labelled, and never synthesize live requirement acknowledgments.

The diagnostic follow-up captures fresh private review/correction profiles. Its
offline regressions compare all independently available output/coverage findings
before the first repair, convergence within the existing allowance, unrelated-edit
rejection and inconclusive Judge recovery without another call. Typed response
constraints and clearer input-readiness descriptions are mechanism changes, not
independent business labels. Keep the original proposal, rejected candidates,
per-purpose call counts and unknown usage in reports. An invalid Judge reference
cannot be counted as a correct review or repaired through a hidden extra call.
Retained live campaigns keep their original settings and failed records; new defaults
must not be retroactively attributed to those campaigns.

The [diagnostic follow-up evidence](../../docs/evidence/taskplan-diagnostics-2026-10-18/README.md)
records the focused regressions, preserved failed attempts and final offline comparison.
It does not dispatch a new live campaign.

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

## Applying a committed pending response

`--schema-portability recover-receipt --workspace <workspace> --campaign <id>
--run <label> --expected-revision <revision> --request-id <exact-pending-id>
--interrupted-ms <uncheckpointed-active-time>` validates and advances exactly one
committed response in memory. Add `--apply` only when authorized to checkpoint
that recovery. The command requires a clean committed build and holds the existing
campaign lease; it creates no provider or MCP transport. A missing receipt, changed
request/schema, wrong tenant/revision, prior execution or inconclusive closure
rejects recovery. Additional inference and discovery are unavailable.
This no-provider operator requires a committed model receipt; it does not parse a
saved raw HTTP response or extend an expired coordinator allowance.

Recovery retains the original source/manifest, request, receipt, requirements,
financial records and allowances. The checkpoint records the validator build,
hashes, previous session/result and interrupted active time. Interrupted time must
come from retained execution evidence, not wall time spent waiting for an operator.
The elapsed allowance is never reset. Repeating an applied recovery is rejected;
a failed checkpoint leaves the committed response available without another charge.
Review readiness or remaining diagnostics end the command—neither execution nor
another paid repair follows. The original live harness's frozen-build checks and
artifact approval requirements remain in force.

## Inspecting an uncertain request

`--campaign <id> --inspect-campaign` reports reservation/receipt counts, pending identities, the known budget snapshot and a hash of the campaign evidence. It is read-only and does not initialize model configuration. `--inspect-run <key> --include-receipts` now includes pending reservations even when no usage receipt exists, along with any retained safe failure metadata. Private request/receipt inspection still must not be redirected to plaintext files.

New failed dispatches retain the existing provider-neutral failure kind, HTTP status, safe provider code, retry metadata and failure stage in encrypted records. Exception messages, raw bodies and credentials are excluded. This does not authorize retries, create a completion receipt or retroactively recover missing metadata. A reservation remains uncertain if completion or receipt persistence fails. Missing-receipt replay exits 2 with `REPLAY_UNAVAILABLE`; no request is sent.

A definite Structured Output schema rejection can be retained with
`--schema-portability retain-schema-rejection --request-id <exact-id> --campaign
<id> --workspace <path>`, under the clean-source campaign lease and without
initializing a model. Besides the existing `invalid_json_schema` code, the verifier
recognizes a single retained HTTP 400 whose dispatch failure is nonretryable
`InvalidRequest` and whose original response identifies `invalid_request_error`,
`response_format`, an explicitly null code and the transport's exact
`Invalid schema for response_format 'output': ` message prefix with a nonempty
schema diagnostic. Ordinary 400s, malformed bodies, successful or unknown attempts,
usage and completion receipts reject this path. The append-only closure binds the
original request, journal and failure hashes; it does not invent a provider code,
completion receipt or usage, or change the original evidence or accounting.

## Historical bounded HTTP recovery

This subsection describes the older captured retry policy and synthetic harness.
Its EUR 50/eight-attempt defaults and mocked integrations do not describe fresh
compact sessions. Those use the captured campaign ceiling, construction
allowances, their captured transport policy and isolated real integrations described in
[incremental construction and isolated delegated review](#incremental-construction-and-isolated-delegated-review).
Historical recovery remains limited to its original issued policy.

The historical KeyVault model uses AI.Core's single HTTP retry loop. Provider `RetryPolicy`
configures total attempts, per-attempt timeout and the uncertain allowance (one by default).
The policy is pinned separately in the campaign. Generation remains synchronous and without
tools; workflow integrations remain mocked. No planner retry phase is introduced.
The adapter selects synchronous generation on its dispatch copy even when the planner
prefers background generation. The original reserved request and response schema stay intact.

Before each HTTP send, an encrypted `planning-evaluation-http-attempts` record stores its fresh
identity and a conservative token/cost allowance. Admission includes verified previous usage,
legacy campaign accounting and every unresolved allowance, with EUR 50 and eight physical
attempts per session. Uncertain original attempts and their allowances remain after recovery.
HTTP errors do not supply measured generated usage; unresolved 5xx attempts retain
their full conservative reservations. Calls count every reserved physical
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
A bounded unknown remains fully reserved; it does not become verified usage.
Subsequent independent runs require a completion receipt or an applicable explicit
inconclusive closure for every earlier reserved request. An unclosed missing receipt
blocks dispatch even when the remaining financial allowance is sufficient. Replaying a
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

An individual request can exhaust its pinned HTTP retry policy before the session exhausts eight attempts. To retain that request permanently without replay or reconciliation, use `--schema-portability retain-exhausted-request --request-id <exact-id> --campaign <id> --workspace <path>`. This explicit action requires a clean checkout and the campaign lease, a saved dispatch failure, exhausted transport attempts containing unknown completion, and no successful HTTP response, completion receipt or verified usage. When later HTTP failures follow the uncertain attempt, the adapter's recorded retry exhaustion, attempt count and terminal status must match the journal. It appends a closure with evidence hashes; original records and full unknown-usage reservations remain unchanged. New request identities may then use the remaining budget. Never use this to claim successful completion or free reserved cost. Campaign regressions cover mixed attempt orderings, rejection, restart, no redispatch, unchanged accounting and the shared spending ceiling.

The captured `MaxUncertainRetries` can exhaust before `MaxAttempts`. Retention also
recognizes that boundary when the last attempt is an unresolved transport/timeout,
the unknown-attempt count exceeds the explicitly saved allowance, and the adapter's
retryable exhaustion and attempt count match. Missing legacy limits, cancellations,
successful responses and unresolved terminal HTTP responses grant no additional
authority. The closure records `http_uncertainty_limit_exhausted`; all original
attempts and their full reservations remain unchanged.

Historical incremental sessions captured retries disabled. A timeout may therefore stop before
the retained campaign policy's attempt or uncertainty allowance is exhausted;
that does not grant eligibility for this archival action or permission to replay.

## Authorized permanent retention of an uncertain planning run

The separate `retain-uncertain-request` operator can permanently abandon one
stopped, unexecuted incremental planning run after explicit authorization. It
requires the exact pending tool-free request, eligible unresolved transport attempts,
matching source/session policies and failure evidence, and every full original
input/output/cost reservation. Successful HTTP responses, usage receipts,
approval or execution evidence reject this operation. Historical retry-exhaustion
eligibility remains unchanged.

The default mode accepts only its historical timeout/transport evidence, with no
HTTP status or body. A separately authorized single HTTP 500, 502 or 504 can instead
use its exact `--retention-kind single-http-500`, `single-http-502` or
`single-http-504` on both inspection and application. The 500/502 modes require
`ServiceUnavailable`; 504 requires `Timeout`. Failure and journal records must
match that exact HTTP status. Each mode requires exactly one exhausted attempt,
no transport-failure marker, and a
bounded recognized error-only JSON body. Accepted bodies contain either only an
`error` object with scalar string/null `message`, `type`, `code`, `param` fields,
or exactly the three string fields `errorCode`, `errorMessage`, `errorDetail`.
A nonempty error message is required. Unknown, duplicate, nested data, completion
or usage fields fail closed; bodies over 65,536 characters are refused intact.
It also requires the original hash-bound effective policy to be present with
retries disabled, HTTP recovery disabled and `MaxAttempts=1`. Other statuses,
multiple or mixed attempts, receipts and verified usage remain ineligible.
Provider availability returning later does not prove termination or usage of the
failed request.

For newly captured confirmed-response retries, use the separately authorized
`--retention-kind exhausted-planning-retries` on inspection and application. The
operator binds all one to three physical attempt identities, the exact captured
policy, source, request, run, failure, journal and accounting hashes. Earlier
attempts must be confirmed transient error-only responses; a transport/timeout
without a response can occur only last and is never replayable. Successful
responses, receipts, cancellation and verified usage remain ineligible. Every
failed attempt keeps its full unknown-usage reservation, including HTTP 429.
The older `retain-exhausted-request` shortcut cannot close this new policy.

An explicit `--retention-kind interrupted-process` handles a corpus planning
process lost after its single HTTP attempt was reserved but before any outcome was
recorded. It requires the exact pending, tool-free request in its original
generating session, with no result, failure, receipt, usage, HTTP response or
execution evidence. The original captured transport policy must forbid retries
and recovery. It binds source and session identities to the
original run key and retained configuration. This mode records missing outcome
evidence; it does not create a stopped session, transport failure or provider
termination claim. Inspection is read-only; application requires the campaign
lease and separate authorization for the exact evidence and full reservation.
The existing closure permanently forbids replay and continuation without changing
any original record or releasing the reservation.

First inspect the exact retained identity without initializing a model, MCP
transport or lease:

```bash
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability retain-uncertain-request --workspace <workspace> --campaign <id> \
  --run-collection planning-schema-portability --run-key run:<label> \
  --request-id <exact-pending-request-id>
```

For a corpus run, use `--run-collection planning-evaluation-runs` and its exact
`<source>:<phase>:<case>:<repetition>` key. Review the returned evidence hashes
and accounting, then repeat with `--apply --expected-evidence-hash <hash>
--operator <identity> --authorization <explicit-authorization>`. Apply requires
the clean tested checkout and exclusive campaign lease. Evidence changes reject
the operation; the same already-applied hash and authorization return the existing
closure without writing again.

The selected HTTP mode is included in its new evidence hash and must match on
reinspection or idempotent application. Omitting the option does not select it
automatically. Historical evidence omits this field and retains its original
hashes and eligibility. The authorization must identify the separately reviewed
request/run and full permanent reservation; this mode grants no retry or
continuation authority.

Only a separate encrypted closure is appended. Original run, request, transport,
failure, policies and financial admission remain unchanged. The full unknown
reservation remains charged against the campaign ceiling; no receipt, usage,
completion or successful result is fabricated. Exact replay and all planning,
review or execution continuation of that run are permanently denied. Inspection
remains available. Independent new identities still require ordinary admission,
and any other unclosed uncertainty still blocks dispatch.

New dispatches capture their effective request transport policy and hash before
the first attempt, including incremental single-attempt/no-recovery behavior.
Those optional fields remain absent on historical records. Historical eligibility
uses retained incremental-session policy and original source evidence explicitly;
the operator never rewrites an old policy to claim exhausted retries.
Preserve the reservation and campaign stop when the existing closure checks fail.

For a stopped live planning run which exhausted all eight session attempts before
execution, `--schema-portability retain-inconclusive --run <exact-run-id>` exposes
the existing failed-session archival operation for the live journal layout. This is
an explicit operator decision, not automatic readiness recovery. It requires a
saved dispatch failure, matching reserved identity, missing completion receipt and
verified cumulative exhaustion. Started executions are ineligible. It appends only
an inconclusive closure; it changes no original request, run, receipt or reservation
and permanently prohibits replay. Pricing readiness alone does not establish that
an earlier uncertain campaign entry has been closed.

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

The [4 October fresh-validation report](../../docs/fresh-live-validation-2026-10-04.md)
records the explicit EUR 100 ceiling, frozen cohorts and failures before execution.
Use fresh identities for subsequent evaluation; never replay their retained artifacts.

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
It shares one encrypted ledger across planning, Flow inference and intercepted
Copilot SDK inference. The initial ceiling was EUR 50; append-only authorized
extensions determine the current ceiling. Missing receipts retain conservative reservations.

An extension requires explicit user authorization.
Record that authorization under the existing campaign lease before collecting a new
cohort; keep the original configuration, cohorts and every usage reservation intact:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability extend-budget --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --from-eur 50 --to-eur 100 \
  --authorization "User explicitly authorized an additional EUR 50 on 2026-10-04."
```

This operator command never dispatches inference. It checks the previous ceiling,
stores authorization history and preserves all token, attempt and permission limits.
Fresh cohort manifests pin the new ceiling; historical manifests remain unchanged.

Build a clean committed checkout before collection. Keep the same source and
configuration for every run within a cohort; a changed candidate requires a new
cohort identity under the same campaign. Do not repeat the original diagnostic.

Fresh Amazon evaluations retain the business request, product bound, fixed output
path, truthful observed data and cleanup. The harness does not inject page-specific
interaction instructions or notice classifications. Review the actual operations
against the request and discovered contracts before approval. Retained runs,
prompts and approvals keep their original semantics; never replay an execution
that has already started. Visit, observation, workbook and cleanup oracles remain
unchanged.

For a fresh session testing Agent.Server's automatic-correction policy, add
`--automatic-correction` to `plan`. The option is captured in that new session;
revisions and execution reuse the saved policy. Omitting it preserves the
historical harness correction behavior. Fresh construction captures the ordinary
or explicitly selected large limits described below. Retained sessions preserve
their original call and correction allowances; artifact approval remains separate.

For captured historical live evaluations, `--business-judge` was added to
`plan` to enable the independent advisory review. It uses the generator's model
and the same eight-call, two-correction allowance, 96,000 input-token limit and
32,768 output-token reservation retained by this live campaign. It does not use
the newer creation defaults of other hosts or receive a separate budget. The
Judge setting is captured in the request and an enabled cohort manifest.
`--no-business-judge` explicitly selects the disabled setting; supplying both
switches is rejected. On `revise` or `execute`, omission reuses the persisted
setting and an explicit mismatch stops before readiness or transport. Start a
new frozen cohort to change the setting. Historical disabled manifests remain
unchanged, and artifact approval remains an explicit revision/hash-bound action.

Live planning measurements separate generation, corrections and Judge calls by
their recorded purpose, preserving unknown usage when receipts are incomplete.
Only the independent execution oracle labels the executed candidate's business
outcome; unexecuted candidates remain unknown. These measurements do not turn
advisory Judge findings into proof of business correctness.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability plan --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4 --case amazon --run final4-amazon-1
# Inspect accepted requirements, the actual TaskPlan, revision and artifact hash:
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability inspect-run --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run final4-amazon-1
# Review explicitly, then supply the approval command written by the reviewer:
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability execute --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4 --case amazon --run final4-amazon-1 \
  --review-command /private/path/review-command.json
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability report --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort final4
```

The review file uses the existing planning command contract:

```json
{"kind":"approve","expectedRevision":7,"artifactHash":"<exact reviewed hash>","reviewedRequirementIds":["<explicitly reviewed requirement ID>"]}
```

Keep the execution terminal attached. Artifact approval does not remove the generated
workflow's runtime confirmation: submit its explicit response (for a confirm form,
`{"response":true}`) only within the reviewer's authorization. The existing execution
deadline includes this wait. Console reads stop waiting at cancellation even when
the underlying terminal read blocks synchronously; a late answer is never accepted.
A timed-out execution remains a failed attempt and cannot be reset or replayed.

For a targeted implementation correction before execution, the existing `--revision-command` file may include `editablePaths`:

```json
{"kind":"revise","expectedRevision":7,"artifactHash":"<exact reviewed hash>","preserveRequirements":true,"editablePaths":["/tasks/compose/inputs","/tasks/publish/requires"],"text":"Use only the compact export and strengthen the publication guard; preserve other work."}
```

These host-selected paths authorize typed replacements only. Task insertion/removal/movement requires a separate explicit global revision. An exhausted or already-started run remains ineligible; neither form resets accounting. Review the new artifact and submit its own requirement acknowledgments before execution.


Before execution, concrete review feedback can use the same planning loop without
discarding accepted intent, discovery or cumulative allowances:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability revise --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort <frozen-cohort> --case <case> --run <retained-unexecuted-run-id> \
  --revision-command /private/path/revision.json
```

```json
{"kind":"revise","preserveRequirements":true,"expectedRevision":7,"artifactHash":"<exact reviewed hash>","text":"Concrete missing binding or failure-path work; retain accepted intent."}
```

Use null for the hash of a stopped session with no artifact. This command requires the
original frozen candidate, rejects started executions and archives the prior session,
result and feedback before further dispatch. Revisions retain the captured planning
allowance, model/repair budgets and aggregate calls, discovery reads and latency.
Historical absence retains the former 30-minute host cap; fresh runs align the host
and outer planning deadline with the captured planner limit (at most five hours).
Recovery subtracts retained active time instead of resetting it. Runtime effects
and their independent 30-minute deadline are unchanged.
Fresh planning journals own deployment and physical admission without an outer
logical usage wrapper. Exact saved completions remain readable after elapsed
admission expires or deployment capabilities change; only new physical attempts
require current admission. Collections reconciles a complete saved HTTP success
before coordinator deadline checks. Historical wrappers remain unchanged.
They require fresh review and approval; the command never creates acknowledgments.

Supply every accepted requirement ID exactly once only after comparing it with actual
operations, loop bodies, conditions and data dependencies. Never generate this list
automatically from the requirements. Missing visits, extraction or observation completeness
require revision; they cannot be acknowledged as covered. The planner rejects missing,
duplicate, unknown or stale submissions before execution. The old `--artifact-hash`
argument alone no longer approves a live run. Inspection includes private retained
content; keep command files and raw inspection output outside the repository.

Use repetitions 1–3 for `amazon` and `code`, preserving every failed slot. Execution
requires a terminal with explicit human answers. Recommendations never approve a
call, and a started execution cannot be replayed. Browser debug `KeepBrowserOpen`
is disabled only in the spawned disposable process so cleanup can be checked.
Code feedback remains local; GitHub publication is excluded and untested.

`inspect-run --run <identity>` returns private retained requests, responses,
execution events and oracles; redirect only to an access-restricted local file.
Its `runtime_llm_inputs` entries report estimated prompt tokens and durable completion
status for ordinary `llm.call` invocations. Inspection performs no dispatch or campaign-accounting mutation;
the public KeyVault reader may initialize storage and append access-audit records.
Estimated input size is not billed usage.
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

### Bounded mapping cohort

First inspect exact deployment metadata and accounting readiness without inference. The harness loads host metadata and persisted Agent user overrides with Server precedence; it keeps the campaign provider/model pinned. Output contains allowances, pricing readiness, the validated exchange quote and fingerprints, never credentials. Missing limits, pricing or a fresh quote stop before MCP execution; campaign ceilings are not deployment metadata.

Validated quotes (currency pair, rate, authority and timestamp) are cached in encrypted campaign records under the existing seven-day freshness policy. Each newly admitted request stores its quote atomically with its HTTP journal before any attempt. Settlement and receipt recovery reuse that admitted quote without fetching another rate. A stale quote cannot admit a new request; a completed request keeps the conversion used for its reservation. No fallback rate or freshness extension is permitted.

A verified pricing/quote refusal before dispatch returns `LLM_BUDGET_UNVERIFIABLE` with `dispatch_status: not_started` and a precise reason. The campaign durably binds that refusal to the original request hash, and Flow records it through the existing completion path so failure cleanup can run. Reusing the same identity replays the refusal, not inference. Changed identities or historical unknown dispatches cannot acquire a new zero-dispatch classification. Failed receipt writes and unverified post-dispatch errors continue to block cleanup and replay. Inspection of retained runs does not depend on exchange-rate availability.

```bash
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --schema-portability provider-readiness --campaign schema-portability-20261002 --workspace /path/to/GnOuGo
```

After deterministic tests and a clean frozen commit, prepare the real-provider/stdio matrix within the existing campaign:

```bash
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability mapping --campaign schema-portability-20261002 --workspace /path/to/GnOuGo --run mapping-cohort-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability mapping --campaign schema-portability-20261002 --workspace /path/to/GnOuGo --run mapping-cohort-1 --variant scalar --review-command /private/path/scalar-review.json
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability mapping-report --campaign schema-portability-20261002 --workspace /path/to/GnOuGo --run mapping-cohort-1
```

Preparation starts a disposable MCP process with no structured output metadata and deterministically compiles three fixtures with zero planning inference. Each `--variant scalar|extended|each` displays its persisted requirements, plan, revision and artifact hash. Submit a separate existing approval command for each variant with explicitly reviewed requirement IDs. No acknowledgment or runtime permission is auto-accepted. An already-started variant cannot be replayed, including after interruption.

The scalar fixture executes cold/warm JSON, changed data/shape, text, HTML and missing-data cases. A separately labelled fault-injection case substitutes one invalid script before real-provider repair. The extended fixture tests a changed target; the each fixture reads 80 complete HTML observations through real MCP, checks every extracted row and repeats for a warm-cache run. The full collection exceeds the campaign input allowance; bounded examples must suffice without truncating execution. All eleven cases and exact expectations are frozen before dispatch. A failed case stops that variant and remains in the denominator. Fixes require new identities; artifacts never change at runtime.

Mapping scripts/attempt receipts persist through the existing encrypted run store under a cohort-specific tenant; measurements and manifests use `planning-mapping-evaluation`. All paid calls use the existing campaign gate. Printed rows contain only public fixture observations. The existing Amazon/code commands and their independent execution oracles are unchanged. Do not pool this matrix with historical 33/33 or prior live cohorts.

The independent Copilot producer probe uses the actual task-enabled MCP, an inference-policy proxy and one disposable Python command. It checks the observed report, command exit, durable receipt and encrypted receipt reload before cleanup. Permission requests remain interactive. Unknown completion retains the directory and blocks replay; the probe is not full code-review acceptance.

```bash
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability copilot-probe --campaign schema-portability-20261002 --workspace /path/to/GnOuGo --run completion-probe-1
```

Only after focused probes pass, freeze a fresh Amazon/code cohort and attempt repetition one of each with unchanged execution oracles. Preserve all failures and the campaign's unknown reservations.

### Compare compiled glue without inference

`--schema-portability replay-compile --compare-bindings --campaign schema-portability-20261002 --run <retained-run> --workspace <workspace>` loads the saved TaskPlan and its contracts read-only, recompiles under v3/v4/v5 and reports steps, sets, workflows, YAML bytes/lines and remaining adjacent set pairs. It neither executes the workflow nor changes saved artifacts, approvals or campaign accounting. Compare the identical plan separately from any changed extraction composition.

## Observed-product oracle v2

Retained `observed-products-v2` runs require navigation arguments to match
previous complete observations, and redirects require the same call's producer
final-navigation evidence. Visits are occurrences, not deduplicated URLs. No
product-path or hostname heuristic participates in this version. Each workbook
row is checked against its own visit: names are exact observed values, descriptions
one complete observed block, and prices exact displayed segments (no punctuation
or whitespace rewriting). UI labels cannot establish producer errors. Missing
facts without independent absence evidence remain incomplete. A complete snapshot
proves capture coverage, not semantic relevance of the selected fact.

Historical `real-workflows-v1` manifests and verdicts remain frozen. Offline v2
replay is separate evidence and performs no Browser/LLM calls. Unknown oracle
versions fail closed. Workbook existence still requires a successful writer receipt
for the expected output path, and live cleanup is independently probed.

Fresh runs capture `amazon-business-v4` or `code-business-v2`. Amazon additionally
binds the search to the exact caller query and checks the first product identities
and their order from complete original search observations against visits and rows.
The original selector and producer metadata must establish a whole-document
`body` or `html` observation without fallback. A complete capture of a narrowed
subtree cannot define the expected first products.
Insufficient independent identity evidence cannot pass. Review PR additionally
checks local report claims against retained command completions and pinned checkout
evidence; valid report shape alone is insufficient. Those deterministic checks do
not establish the quality or completeness of the requested diff review. Business
acceptance also requires independent assessment of the retained pinned diff and
actual local report; missing evidence remains unverified. Historical oracle versions and
verdicts stay unchanged. Paired comparisons must apply the same frozen strengthened
checks to both arms; missing baseline evidence remains incomparable.
Fresh code-oracle evidence retains the parsed report actually read from disk and
its compact-JSON hash in the encrypted run. Comparison checks verify that hash
before inspecting the report; they never reconstruct it from write arguments.

## Incremental construction and isolated delegated review

Fresh live sessions use version-two compact incremental construction and mandatory bounded local and
intermediate reviews. The historical `--business-judge` setting cannot disable these
reviews or introduce a final whole-plan Judge. Every draft, review, evidence request
and correction uses the same captured session and cumulative campaign allowances.
An oversized draft evidence batch may request a smaller exact view without spending
candidate-repair capacity; outstanding semantic findings retain their repair limits.
A rejected mandatory Judge request is reported with its host admission cause and
has no provider receipt or business verdict. A larger ceiling requires a separately
frozen fresh profile, never an in-session change or silent context truncation.
The retained `47c08b428` Code diagnostic stopped on a complete mandatory request
estimated at 18,131 tokens against its 18,000-token draft ceiling. A subsequent
20,000-token draft profile retains the 8,000 target and all other phase settings.
If paired with changed argument-meaning guidance, report both changes; the run
cannot isolate their individual effects. Preserve the original rejection and
independently observed accepted-binding defects even when a later run succeeds.
The subsequent `f0644826b` Code20 diagnostic stopped after 47 received requests and
four corrections, without an artifact or execution. Its largest draft was 12,543
estimated tokens: this run supplies no evidence that the higher ceiling helped.
It repaired a PR-URL/Git-remote mismatch but accepted an unwanted duplicate clone;
the final consumer was correctly rejected for nullable type and missing directory
provenance. Preserve these findings separately from later producer-port presentation
and exact-citation normalization changes. Neither technical tests nor better navigation
establishes that the duplicate-effect business miss is fixed.

Fresh cohorts capture presentation revision 5. Its required Judge action and separate
citation domains are interpreted from the issued schema; raw reports remain retained.
Combined review coverage counts as one logical request, while every retry remains a
physical attempt. Before delegated approval, the ordinary artifact verifier must
accept the retained candidate-bound review coverage. A final-review state or static
oracle pass alone cannot bypass that verifier; retain a failed verification without
issuing review metadata or executing. Report prior-defect resolution, persistent findings and inconclusive
reports separately; no checkpoint increases the independent business denominator.
Retain unnecessary accepted effects as business failures even if later invalidation
detects them: rollback reconstructs accepted commitments, rather than deleting them.
Bounded exact accepted-operation context is a presentation experiment, not a
deterministic duplicate detector or proof of avoided corrections. Report prepared-only
request measurements separately from actual admission rejections and dispatches.
The earlier revision-4 behavior remains frozen for historical cohorts. Scoped reviews receive compiler
facts separately from business and runtime evidence, plus bounded exact caller
declarations. An explicit inconclusive review is not acceptance. Its single
possible reassessment uses only already relevant omitted exact support, retains
its request and evidence hashes, and shares existing call/token/time/financial
admission; it does not charge a correction or grant execution authority. Count
those calls and retained failures in the same phase measurements. Fresh findings may
use their validated concrete location as the implementation citation, paired with
an original obligation; retained issued schemas preserve their earlier citation
rules and report bounds. Historical revision-3 cohorts remain unchanged. Freeze a new source/settings identity for
this change; deterministic tests or a completed review do not establish a live
business improvement. Fresh discovery bindings constrain task-kind/operation/group
field combinations to the existing semantic rules; retained flat-schema requests
still resume byte-exactly and undergo the same semantic validation. The required independently executed paired outcomes and
existing delegated approvals remain the acceptance gate.
Use `--construction-preset large --automatic-correction` only for an explicitly
authorized fresh cohort: it captures 384 physical planning attempts, 64 corrections,
the same compact phase input limits as ordinary sessions, 8,192 draft/correction output tokens, 2,048 Judge
output tokens, 15,000,000 aggregate tokens and
five hours. The request, cohort manifest and immutable transport-policy record must
agree. Continuations cannot raise a captured allowance. Sessions with existing HTTP
journals retain their original eight-attempt policy when no newer policy was captured.
The preset never raises the campaign's monetary ceiling or clears reservations.

The optional delegated reviewer exists only in this benchmark executable. Production
approval retains the existing explicit human requirement acknowledgments. After an
actual review of bounded construction evidence and independent scenario obligations,
an authorized reviewer supplies two private JSON files to `execute`:

```sh
--delegated-authorization /private/path/authorization.json \
--delegated-review /private/path/artifact-review.json
```

The authorization pins `id`, the user's explicit `userAuthorization`, `reviewer`,
`campaign`, `run`, `scenario`, absolute `workspaceRoot` and frozen `sourceRevision`.
The review has a `receipt` using `PlanningApprovalReview` and `evidence` with explicit
`requirements` and `tasks`. Each requirement review records its original content
hash, independently assessed scenario obligations, the assessment
`accepted_with_recorded_uncertainty` and unresolved aspects. Each task review records
its exact content hash, concrete review evidence and assessment `reviewed`. Receipt
hashes use SHA-256 over the source-generated canonical JSON; its origin is
`delegated_test`, and it binds tenant, run, reviewed revision, artifact and explicitly
acknowledged requirement IDs. Neither file is generated from Judge opinions or by
blindly copying generated requirement IDs. Failed or incomplete evidence stops approval.
The immutable authorization and review are retained through the encrypted campaign API.

Amazon authority permits Amazon.fr search/product reads, Browser cleanup and the exact
cohort workbook destination. It rejects clicks, cookie/account interactions and purchases.
Code authority permits the pinned SmartGuide checkout, local inspection/checks/report and
confined clone cleanup; it rejects publication and permission expansion. Concrete tool
arguments are checked again before transport. Existing producer policies still apply.
Fresh Code prompts explicitly require the single `git_clone` to set `branch` to the
frozen head and `targetDirectory` to the fixed cohort repository path. Older issued
prompts remain unchanged. This scenario-input clarification requires a new frozen
cohort and the same revised input in both comparison arms; authority and oracles
are unchanged.
Synthetic delegation has no external-producer authority.

This external benchmark host lists only tool methods permitted by that same authority;
it preserves the original tool contracts and does not expose MCP prompts or adaptive
agent runners. Local computation remains available. The method filter does not authorize
arguments, callbacks or execution. Production discovery is unchanged; historical run
continuation still requires its exact retained source revision.

The existing host-owned artifact confirmation is still shown for the exact reviewed
artifact and single execution. Correlated Copilot permission dialogs require the
reviewer to inspect the actual operation and reply `Allow once` or `Refuse`; sandbox
bypass, broad grants, unrelated dialogs and responses after interruption are rejected.
Each response is recorded with delegated provenance and the artifact/authorization
hashes. There is no automatic approval, new executor or whole-workflow model Judge.
Uncertain effects and started executions remain ineligible for replay.

The in-memory `local` and `collections` corpus cases also accept these two files
with `--review-run <exact-run-key>`. Their authorization uses scenario `synthetic`
and the actual saved session ID. This route rejects operation tasks and supplies
no runtime confirmation provider; unexpected effects or confirmation requests
cannot acquire authority. Independent nominal and alternate input oracles still
check the exact results. Other corpus cases retain their existing approval path.

Fresh corpus records additionally capture `synthetic-artifact-v1` before planning.
For isolated local/collections artifacts, this supplementary check rejects external
operations and interpretation/extraction tasks requiring an unconfigured runtime
LLM client. Collections also requires result-contributing parallel per-item calls
to a reusable group; an unused demonstration loop does not satisfy the request.
The artifact-bound verdict is retained separately from the execution variants and
does not authorize approval. Historical records without this oracle version retain
their earlier verdicts. Paired comparisons apply the same frozen supplementary
check to both revisions alongside the existing independent runtime oracles.

The active local product fixtures contain no cookie-consent work. Historical saved
artifacts retain their issued vocabulary for compatibility tests; those fixtures offer
no consent control and never exercise a consent action.

Ordinary fresh sessions capture 64 calls and four corrections. Complete serialized
input targets are 3,000/8,000/4,000 for navigation/draft/Judge families; hard
ceilings are 6,000/16,000/8,000. Targets neither reject requests nor trigger another
call. Stricter explicit limits prevail. Initial reasoning is medium and output
reservations are 8,192/8,192/2,048. The 15,000,000-token/five-hour cumulative budget,
graph capacity and runtime artifact-confirmation gate remain unchanged.

`--phase-policy <JSON file>` supplies the complete Core phase-policy DTO before
creation. `--cohort <label>` optionally namespaces Collections run/configuration
keys; absence preserves historical keys exactly. External scenarios retain their
existing cohort/run identity controls. Settings, source, model deployment, MCP
binaries, inputs and oracles are frozen per arm. Retained runs reject conflicting
settings. `provider-readiness` reports exact deployment `supports_reasoning_effort`
and `supported_reasoning_efforts` declarations; null remains unknown. A profile
may use only a declared supported level. Supported low navigation reasoning is a separate experiment; larger
output reservations require verified exhaustion evidence and a fresh identity.
No profile changes settings midway through a session.
If necessary original evidence exceeds a hard input ceiling, a separately frozen
profile may test a higher ceiling while retaining the presentation target. Record
the rejected complete envelopes and all changed settings; do not trim mandatory
constraints, retry the failed identity or infer architecture-only causality from
a profile that also changes source.

The declared-configuration comparison mode records intentional per-arm settings
differences, while preserving shared safety, call, correction and cumulative limits.
Its outcomes compare configurations, not architecture alone. Default matched mode
still requires identical effective reasoning/output settings. All nine candidate
runs in the agreed 18 alternating runs (nine pairs) must pass independent business oracles;
missing baseline evidence is explicitly incomparable. See the maintained
[convergence summary and evidence index](../../docs/evidence/compact-taskplan-convergence.md).

Fresh sessions capture the Core confirmed-response transport policy before freezing:
at most three physical attempts, no uncertain retries, a ten-minute per-attempt
response timeout and bounded delay. The journal admits each attempt against the
captured physical-call and 15,000,000-token ceilings, remaining planning deadline,
financial allowance and the exact hash-bound mandatory review count. A local
Judge can reserve its required intermediate review. Review headroom uses the
same admitted price/FX quote and is not a charge. Explicit single-attempt requests
remain stricter; runtime and inference-proxy behavior remain separate.

Every failed attempt under this new policy keeps its conservative input/output/cost
reservation, including HTTP 429 and confirmed server errors. A successful receipt
settles only its own attempt. Campaign, run, phase and partial usage reports derive
the same bounds from original journals. Historical policies and issued requests
retain their original accounting and retry behavior. Recovery may parse an exact
saved complete response, or continue its eligible confirmed-error retry under the
same remaining limits; missing responses and uncertain completion cannot be resent.
Any permanent abandonment still requires the exact operator authorization above.
Fresh presentation schemas restrict selected requirement identities to the supplied
original anchor/evidence or an existing bounded reselection. Fresh evidence requests retrieve up to eight coherent addresses after prospective
complete-request admission; mandatory schema closures are never truncated to fit.
Successful individual evidence identities prevent regrouped repeats, and valid
evidence-only responses consume calls, tokens and money without another correction.
Revision-4 Judges narrow an oversized evidence batch to one address on the next
request, preserving the rejected measurement and exact admission diagnosis. An
oversized singleton still stops; this evidence loop grants no correction authority.
Historical issued domains and multi-address batches remain
recoverable. These presentation changes require new frozen diagnostic identities,
not retries of an unchanged failed request.

Delegated runtime permission also requires one exact currently active host-owned MCP
invocation, matched by tenant/run/step/server/method and its child dialog identity.
Copied artifact suffixes, ended calls and ambiguous parallel dialogs cannot authorize
anything. URL-less Browser reads require the exact cursor issued by a successful
allowed Amazon observation, bound to its original URL and snapshot identity; navigation,
closure or error clears that authority. Arbitrary current-page reads are rejected.
The independent post-execution closure oracle has a dedicated host-owned empty-argument
probe which never navigates and cannot be selected by a plan or model.

The corpus, stabilization and real-workflow entry points share the same construction
preset, phase limits, cumulative budgets and policy capture. Their configuration
round-trip preserves lower explicit allowances and historical configurations without
construction fields. `--workspace` binds campaign records, model configuration,
the process lease, execution storage and delegated authorization to the same absolute
workspace. Omission retains the existing default GnOuGo workspace.
Before a draft or correction is recorded, the benchmark also checks the existing
campaign upper bound against the mandatory review allowance without creating a request,
reservation or completion receipt. The physical journal repeats admission immediately
before dispatch using the exact retained price and currency quote.
Fresh physical transport admission uses the exact hash-bound `RequiredReviewCalls`
captured on the issued request, including an intermediate review when required.
Historical requests retain their original conservative two-review allowance.
This headroom never becomes a reservation or charge.

Every constructed compact request has safe metadata recording its phase, selected
substep, exact request/schema hashes and selected contract versions. Component byte
counts partition the complete serialized UTF-8 request: instructions, original intent
and requirements, descriptors and exact contract evidence, response schema, interfaces,
fragment/evidence/diagnostics, and envelope overhead. The versioned conservative token
estimate is admission evidence; provider input/output/reasoning usage remains a separate
whole-call measurement. Rejected requests are measured before dispatch and incur no
invented usage. Judge is a phase dimension, not a second token total. Aggregate
peaks, percentiles and component sums include rejected prepared requests. Reports
separately identify admitted request counts, admitted peaks and their limit checks;
an oversized rejected request cannot be hidden behind a smaller admitted peak.

Useful-work reports divide logical planning calls, known physical transport attempts,
provider tokens, cost and known
planning time by distinct independently authored business obligations covered by an
accepted artifact review. They reuse the frozen delegated scenario obligation IDs;
generated tasks, requirements, checkpoints and duplicate mappings never increase
the denominator. Structural validation alone earns no credit. Unknown coverage or
usage remains null, and a zero denominator yields null ratios. Artifact coverage
retains the review's uncertainty and is separate from the existing runtime-oracle
result; it grants no authority. Original records are not reclassified. Fresh corpus
execution rows also capture planning time before approval/execution and measure each
runtime variant directly; historical rows without those timings remain unavailable
rather than receiving reconstructed durations. A resumed approval preserves the
previously measured planning duration.

Compact diagnostics also distinguish interface-scope failures, advisory Judge
findings, non-progress stops and empty output-limit receipts from transport
uncertainty. Preserve these outcomes even when every request fits its phase cap.
For revision-4 review diagnostics, compare findings against the exact
`constructionStage.closeRequirementIds`; requirement associations and operation
commitments do not claim completion. Classify a false positive from the original
claim and implementation evidence, never from pointer validity alone. An observed
false positive does not establish how many corrections a later change avoids.
Automatic operation directories contain the current substep's queried results;
cached originals and exactly issued historical evidence remain retrievable.
Keep source-description provider actions, forward operation-page navigation,
isolated group inputs and existing loop defaults/safety semantics visible in
bounded planning/review context. A corrected evidence-address error must not
consume another correction on subsequent successful navigation. These generic presentation
corrections do not relax compiler validation or prove live convergence. Regressions
also verify that catalog expansion cannot reoffer unchanged consumed
pages, that discovery omits duplicate draft metadata, and that foreach-body
closure reviews explain the existing ordered array exports. Repeated evidence
without demonstrably new knowledge stops without spending a repair. A targeted
original-description reread after new allowed operation identities or versions
must be bound to the issued request, charged normally and blocked again within
that same knowledge set. Cursor/order changes and empty provider pages grant no
new read. Judge evidence addresses remain distinct from finding JSON Pointers;
bare producer IDs are still inconclusive. Compare complete serialized
requests, including content-address overhead and the issued compatibility schema;
a smaller individual component does not establish complete-request admission.
Recorded failures and accounting for Amazon, Collections and Review PR are in the
[convergence summary](../../docs/evidence/compact-taskplan-convergence.md), which links
immutable earlier diagnostic records. The e6b snapshot adds
[Collections output exhaustion](../../docs/evidence/compact-taskplan-e6b24bb77-collections.json)
and [Amazon's pending transport outcome](../../docs/evidence/compact-taskplan-e6b24bb77-amazon-pending.json);
neither produced an artifact or executed. The later exact authorized Amazon closure
is appended separately, retaining the pending snapshot and every reservation.
The `40bd4a048` Code diagnostic retains 36 receipts, three corrections and no artifact
or execution. Its supported sibling-write findings and repeated provenance rejection
are distinguished from two presentation defects: lost prior technical feedback and
an incorrect validation-pass statement during repair. Fixing those defects does not
establish a causal benefit or an avoided correction. Technical passes remain separate
from independent business acceptance.

Exercise automatic structural closure through the coordinator, receipt recovery and
existing artifact approval, rather than assigning candidate identity in the fixture.
Review presentation must retain applicable runtime guard semantics when inspecting
a later consumer or closure. An unspecified collection length is not an authoritative
unlimited-capacity requirement; explicit capacity conflicts still require review.
Preserve known output-exhausted receipts separately from uncertain transport and
never infer the cause of reasoning exhaustion from an empty response alone.

Revision-5 diagnostics distinguish selection mistakes made with complete accepted
evidence from evidence omitted in a later review. Current-step companion operation
links locate original contracts; they grant neither execution authority nor finding
grounding. Follow a rejection through candidate-free reselection and explicit
consumer resumption, but end its replacement chain on acceptance. Preserved repair
witnesses are advisory evidence, not a mandate to reproduce a prior conclusion.
Report request-limit failures separately from semantic findings and keep the
failed request's captured ceiling unchanged.

The compact live comparison freezes baseline `56942f5` and each candidate separately,
with identical model, MCP binaries, scenario inputs and independent oracle versions.
Both receive 64 calls, unchanged correction and ordinary graph limits; baseline
retains its 32k input ceiling. The selected declared-configuration mode compares
baseline's retained settings with the best validated candidate settings, disclosing
any reasoning/output differences. Default matched mode requires those settings
to be identical. After successful candidate diagnostics for
Collections, Amazon and Review PR, run three alternating baseline/candidate pairs per
scenario under fresh run identities. Source changes require a new candidate cohort.
All nine measured candidates must pass independent business checks without unwanted
effects or input-limit violations. Compare peak generation/Judge sizes per scenario
where baseline evidence exists; improvement in one family cannot hide regression
in another. Absent baseline Judge evidence is explicitly `not_comparable`. Report
p50/p95/maximum sizes, calls, corrections, actual tokens, cost
and planning/runtime latency. Include cumulative totals and known partial sums as
well as distributions, both per scenario and across the cohort. Unknown usage keeps
the complete total unknown. Measurements deduplicate within one run; identical
request hashes across independent runs still contribute separately. More calls
are allowed. Higher cost or latency remains
visible, and missing/failed/uncertain runs never establish success. All attempts share
the existing encrypted campaign accounting, including diagnostics and retained failures.

The read-only `--compact-comparison <sanitized-rows.json>` entrypoint accepts `cases`,
`baseline` and `candidate` arrays. Each row retains its exact source/run identity,
effective configuration, independent business outcome, safety findings, whole-call
usage and `request_metrics.requests`. It requires three runs per case in each arm,
matched cumulative/graph allowances and a shared frozen comparison identity.
Output allowances must also match unless the declared-configuration mode records
the intentionally different frozen settings for each arm.
The original harness measurement hashes remain evidence: because harness files
change in this refactor, they cannot establish unchanged oracles by themselves.
Pin independent case/oracle sources, actual producer binaries and model configuration
separately. Preserve exact per-run prompt hashes; any comparison template may replace
only its explicitly declared isolated run destination, with all other text unchanged.
Collections may use fresh `--phase fixture` identities with the real configured model
for this selected comparison; its rows must still report `mode: live`. This does not
claim the historical eight-case measured pilot gate passed. The report is inconclusive
when required outcomes or usage are missing, and three repetitions remain descriptive
evidence rather than a general reliability claim.
