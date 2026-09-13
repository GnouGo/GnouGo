# CodeReview convergence benchmark

The current recursive-partition rerun (`schema5-recursive-partitions-rerun-1`)
permits exactly one fresh Stage-1 classifier session after offline validation and
binary freeze. It uses the unchanged frozen scenario, model, catalog and policy,
all-low reasoning, and 12,000/8,192 ceilings with a 9,600-token input target.
Correct exact-revision behavior acceptance is an intermediate checkpoint; a separate
`campaign advance 1` continues the same session. Final approval requires all six
independent fixtures and their exact artifact hash. The first meaningful blocker
stops the campaign; no replacement start, Stage 2 or reasoning A/B is permitted.

Production remains frozen at `0f4ebfdbf733687c0dc4d1dc9c4b89ca702df95e`.
Build harness changes with `-p:BuildProjectReferences=false` and verify all production
DLL hashes against the archived manifest. The fixture fingerprint includes the
harness assembly identity; record its new value alongside unchanged fixture source,
input and assertion fingerprints. Do not rebuild production for this rerun.

The harness observes the initial missing-threshold finding by code, rule and current
canonical field. It permits the normal repair, then stops before another advancement
if a verified, assessed response leaves that finding or is rejected. An unverifiable
request stops without redispatch. Observation changes only campaign state, never
planner inputs, assignments or budgets. Reports distinguish verified usage subtotals
from unknown dispatch usage.

The previous `schema5-recursive-partitions-20260913` campaign consumed its only start.
Correct behavior was accepted at revision
41 and construction continued. The session stopped at revision 105 when the first
targeted dataflow repair received `LLM_PROVIDER_SERVICEUNAVAILABLE` without a
verifiable receipt. The five-decision truncated intent page split successfully into
two completed children with no repair charge. No final artifact or candidate
execution approval exists. See the [result and offline evidence](../../docs/planner-recursive-partitions-validation.md).

Reports distinguish initial pages, semantic corrections and recursive output
partitions, including effective gates, parent/child identities, partition depth and
terminal singleton truncation. Partition dispatches consume global budgets without
another repair charge. Historical missing origin remains `Unknown`, with no refunds.
Completed siblings are validated and reused through their retained assignments and
request receipts. `campaign audit-replay CAMPAIGN STAGE REVISION` reads an archived
campaign without resuming it or substituting synthetic test responses for receipts.

The following source-authority campaign is historical and remains blocked.

The source-authority campaign authorizes exactly one fresh standalone classifier
session, with all reasoning profiles `low`. Stages 2 and 3 and diagnostic A/B requests
are disabled. Production commit `25566657965b39774fae16f7a553622db66597dc` and isolated campaign
`schema5-source-authority-2556665` are pinned after offline checks. Stage 1 accepts only a
justified typed outcome; `FinalReview` requires all six independent execution cases
and exact artifact-hash approval before `ValidWorkflow`. A technical stop consumes
the start and prohibits further advancement or replacement sessions.

Offline prerequisites: 3,077 passing solution tests, one optional live test skipped,
warning-free solution/harness and package builds, Native AOT planning/encrypted
persistence and trimmed Agent.Server EF persistence smokes. The reference fixture
selfcheck passed without model calls. Public runtime interfaces, token ceilings,
budgets and scenario inputs remain unchanged.

This campaign has consumed its only start. It passed policy preparation and reached
behavior review after six completed calls. Review was withheld because overlapping
input declarations became two threshold ports and descriptive clauses became extra
outputs. The isolated campaign is blocked; its original session remains unapproved
at revision 32. See [the source-authority result](../../docs/planner-schema5-source-authority-live-validation.md).
No executable construction, Stage 2, replacement session or A/B request followed.

Capability preflight uses Agent.Server's injected local resolver. Bootstrap reads
KeyVault provider settings and Agent's persisted default model and metadata overrides;
it creates no metadata HTTP client or model-list catalog.

Archived campaigns remain untouched:

- `schema5-scoped-confirmation-20260913`: policy subjects became operations and
  rejection consequences became forbidden confirmation; see
  [the scoped-confirmation report](../../docs/planner-schema5-scoped-confirmation-live-validation.md).
- `schema5-ee487c8`: model-capability preflight failure, no session start; see
  [the progressive report](../../docs/planner-schema5-progressive-validation.md).
- `schema5-local-metadata-20260912`: unjustified Stage-1 clarification; see
  [the metadata report](../../docs/planner-schema5-local-metadata-live-validation.md).
- `schema5-clarification-20260912`: false `CONFIRMATION_POLICY_CONFLICT`; see
  [the clarification report](../../docs/planner-schema5-clarification-live-validation.md).

Strict replay of the scoped-confirmation campaign at revision 11 reproduced its
conflict before correction (one retained receipt). Corrected replay stops at
`INTENT_SOURCE_AUTHORITY_UNPROVEN` before consuming the old scope receipt: historical
obligations have no current grounding proof. Both runs made zero provider dispatches
and left the source session, journal and budget unchanged. Synthetic complete-clause
regression responses never replace historical receipts. All archived campaigns retain
their original identities and encrypted state.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign selfcheck
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign freeze
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign start 1
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign report
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign inspect 1
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign accept 1 EXACT_REVISION EXACT_BEHAVIOR_HASH
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign execute 1
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- campaign approve 1 EXACT_REVISION EXACT_ARTIFACT_HASH
```

Do not repeat these commands for another stage or replacement session.
`campaign justify STAGE REVISION JUSTIFICATION` records an evidence-backed review of
a non-workflow outcome; it cannot answer a missing business choice. `campaign advance
STAGE` resumes the same verified owned session without a new start. A crash during
creation permits lookup of the uniquely named persisted session, never a second
creation attempt. `campaign replay STAGE REVISION` is receipt-only; missing evidence
stops it. The campaign refuses `campaign ab` before resolving or dispatching a request.

If preflight fails before a session or manifest exists, `campaign archive-preflight
CAPTURED_ERROR_FILE CAPTURED_BINARY_HASHES_FILE` archives the already observed failure
without a provider call. Capture the attempted binaries before rebuilding audit code.
The exception and endpoint remain encrypted; the report contains only a failure code,
location and fingerprint. The resulting campaign refuses all further dispatches,
including a repeated freeze. It cannot create or recover a planning session.

Campaign state and fixture inputs use encrypted `agent-planning-progressive-*-v5`
records under tenant `planner-progressive`. The isolated EF index is workspace-resolved
`.GnOuGo/data/planner-progressive/schema5-source-authority-2556665/gnougo-planning-v5.db`.
The earlier database remains untouched. The historical export copies
only immutable benchmark inputs; no historical session, reservation or budget is
migrated. The saved classifier's public caller default (100) becomes the standalone
classifier's optional public threshold; its original callee required that argument.

`campaign report` contains redacted counts, identities and diagnostics. Engine decision
counts cover active executable holes with persisted deterministic resolution origins;
other engine decisions remain unknown. Model page decisions and executable-hole
exposures are separate measures. Planning and independent execution usage are
attributed separately; this campaign authorizes no diagnostic request. `describe` and `inspect` contain private fixture
or session content: filter them in memory and never redirect them to plain files.

This explicit live harness drives `PlanningSessionService` from Agent.Server using
its KeyVault-configured model, encrypted EF Core session store, durable request
journal and normal planner. It has no alternate planning path. Discovery uses the
frozen catalog. Independent execution uses synthetic frozen transport observations
and has no external business transport. The fixtures make no claim about the current
contents or results of the supplied PRs. The harness cannot publish a GitHub review.

`capture` reads a saved session once and writes an immutable encrypted evidence copy
in the `planner-benchmark` tenant. It never advances or changes the source session.
The isolated index defaults to `.GnOuGo/data/planner-benchmark/gnougo-planning-v5.db`,
resolved through `GnOuGoWorkspace`; `PLANNING_BENCHMARK_DATABASE` can override it.
Schema-4 evidence is inspected only through `scripts/planner-schema4-audit.sh` at
the pinned historical commit. The current harness has no historical decoder.
Before capturing schema-5 evidence, run `scripts/planner-schema4-audit.sh --verify-fixtures`
for the frozen catalog check. This inspects schema-4 evidence without importing a session.
The evidence retains the original intent and frozen catalog. `CodeReviewPolicy.txt`
states the execution cases and review policy explicitly.
The user-authorized `git_compare_refs` followed by `copilot_review` constraint is
part of that benchmark policy only. It does not filter discovery, change the saved
catalog, or introduce a production selector. Production still considers every
eligible implementation and pauses before any oversized dispatch.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -p:SkipClientBuild=true -p:SkipModelMetadataGeneration=true -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- capture SOURCE_SESSION
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- start CodeReviewConvergence01
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- inspect SESSION
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- summary SESSION
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- replay SESSION CAPTURED_REVISION
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- verify-fixtures frozen
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- command SESSION accept_behavior EXACT_BEHAVIOR_HASH
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- command SESSION revise "Concrete behavior revision"
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- resume SESSION
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- execute SESSION PR_URL_INPUT_PORT REVIEW_INSTRUCTIONS_INPUT_PORT
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- command SESSION approve EXACT_ARTIFACT_HASH
```

Review the behavior and executable evidence at each human gate. `inspect` writes
private snapshot content to stdout: filter it in memory, and do not redirect it to
plain files. Progress output contains only session/revision, phases, counts, hashes
and diagnostic codes/locations. Keep failed and unverifiable attempts in the report;
never retry an unverifiable dispatch or reset its budget. The earlier CodeReview-only
campaign required three independent successful sessions; the current scoped-confirmation campaign
authorizes only its single Stage-1 start. A preparation checkpoint alone is not a
successful benchmark.
`summary` emits redacted usage and convergence counts without prompts, responses or
candidate payloads. `report` includes individual durable receipt identities and usage.
`replay` runs the production planner in memory from an immutable captured revision,
using its frozen catalog and exact encrypted request/receipt pairs. Use the revision
containing the pending reservation to reproduce a stopped response. The planning
index is opened read-only; no provider, budget sink, journal writer or session writer
is created. New identities, changed request contents and missing receipts stop replay
without a fallback. Truncated and invalid receipts pass unchanged to the production
validators. Human review remains a stopping point. Output contains only redacted
state, diagnostic locations and replay counts; it verifies the saved session, call
index and budget stayed unchanged. In-memory replay accounting is not new live usage.
Running `replay` on an already waiting revision performs no advance.

Live commands (`start`, `resume`, `command`, `execute`) require separate authorization
after an offline evidence boundary; replay never invokes them. The current CodeReview
run is stopped, and no new live session is authorized automatically.
Provider HTTP 400 details observed by the harness are retained only in encrypted
benchmark records. `inspect-rejection SESSION` reads this private evidence to stdout;
filter it in memory just like `inspect`. A rejection is never a completed model receipt.
`revise` and `resume` use Agent.Server's persisted background revision queue; ordinary
benchmark advancement is explicit. Run only one background benchmark host per
isolated database, since restart recovery discovers every active session there.

`execute` requires the exact deterministic YAML at final review. It independently
executes both PR inputs across passing, mixed, empty, boundary, rejected,
omitted-default, invalid-input, setup-failure and execution-failure cases. Frozen
observations assert one clone, actual fork/head/base selection, observed manifests,
dependency installation, available linters/tests, original comparison payloads,
instruction forwarding, confirmation, publication decisions and owned cleanup.
The synthetic Git history permits either a head clone or a base clone followed by
fetching and checking out the head in that same clone. Manifest search returns
matches from the frozen file contents; it cannot certify commands from filenames.
Each case's extraction/model requests use the existing encrypted Agent.Server
journal and budget store with an artifact/case identity; restart replays receipts.
Failed evidence is encrypted before assertion. Approval requires all 18 cases for
the exact current artifact hash. Execution receipts and approval evidence also bind
the frozen catalog hash and the embedded fixture-source hash, so changing fixtures
cannot reuse old passing evidence. `verify-fixtures` validates the fixture responses
against the frozen producer schemas without model calls; it is not workflow execution.

## Archived canonical declaration Stage-1 campaign

The fresh canonical-declarations campaign permits one standalone-classifier start.
`campaign accept 1 REVISION HASH` submits exact behavior approval once and stops
with `accepted_behavior`, a skeleton hash and no executable dispatch. This checkpoint
is not `ValidWorkflow` and does not unlock Stage 2. Restart cannot advance a blocked
or accepted campaign. `campaign reject 1 REVISION HASH CODE` records an independent
review mismatch without changing the saved planner candidate.

`campaign audit-replay CAMPAIGN STAGE REVISION` is read-only receipt replay of a
named archived campaign. Changed requests require matching retained evidence; no
synthetic response or provider fallback is allowed. Production, catalog, policy,
model, ceilings and all-low campaign reasoning are frozen before the single start.

The [canonical-declaration campaign report](../../docs/planner-canonical-declarations-live-validation.md) records its one technical stop and receipt-only replay. That campaign is blocked; it must not be resumed or replaced automatically.

## Omission-default Stage-1 campaign

`schema5-omission-defaults-20260913` permits exactly one fresh Stage-1 start. It
uses the same model, classifier, policy and ceilings with all-low reasoning.
Acceptance requires independent inspection of the exact behavior revision/hash.
`campaign accept 1 REVISION HASH` records an intermediate `behavior_accepted`
checkpoint and skeleton without dispatching executable requests; a separate
`campaign advance 1` continues that same session. Restart records a durable
acceptance once and never resubmits it.

At FinalReview, `campaign execute 1` must pass all six independent fixture cases
before `campaign approve 1 REVISION HASH` can approve the exact artifact. The first
technical/review/execution blocker closes the campaign. No patch/replacement loop,
reasoning A/B request or Stage 2 is authorized. Archived `accepted_behavior` and
blocked campaigns remain closed. See [omission semantics](../../docs/planner-omission-defaults.md).

The [omission-default campaign report](../../docs/planner-omission-defaults-live-validation.md)
records its one blocked session, partial decisions and receipt-only replay. It must
not be resumed or replaced automatically.
