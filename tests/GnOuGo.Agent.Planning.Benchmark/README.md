# CodeReview convergence benchmark

The newly authorized progressive schema-5 campaign pins production to
`5034d52d2637b1b20df2bdc60c387aa0f23f583a` and uses the separate campaign identity
`schema5-local-metadata-20260912`. Use only `campaign` commands for this
campaign: one standalone classifier, then one capability-backed batch processor,
then one CodeReview session. Stage 1 accepts a justified typed outcome; Stage 2
requires `ValidWorkflow`. A technical stop blocks subsequent starts and advancement.
The harness profile is low for every planning phase; production defaults are unchanged.
Capability preflight now uses the injected local resolver. Its bootstrap reads KeyVault
provider settings and Agent's persisted default selection and model overrides, matching
Agent.Server. It creates no metadata HTTP client or model-list catalog. This code
correction does not unfreeze or restart the archived campaign below.
The earlier `schema5-ee487c8` campaign stopped during model-capability preflight, before any session
start. See [the progressive validation report](../../docs/planner-schema5-progressive-validation.md).
`campaign archived-report` reads that unchanged campaign; it has no dispatch path.
The commands below operate on the new campaign and cannot restart the earlier one.
The new campaign stopped after an unjustified Stage 1 clarification; Stages 2 and 3
remain unstarted. See [the live validation report](../../docs/planner-schema5-local-metadata-live-validation.md).
Its single-start reservation is consumed; these commands do not authorize another attempt.

```sh
bash scripts/planner-schema4-audit.sh --export-progressive
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

Repeat those explicit commands for Stage 2 only after Stage 1 passes. For Stage 3,
`campaign execute 3 PR_INPUT_PORT INSTRUCTIONS_INPUT_PORT` runs the existing 18
CodeReview fixtures. `campaign justify STAGE REVISION JUSTIFICATION` records a
reviewer's evidence-backed assessment of a non-workflow outcome; it cannot answer
a missing business choice or pass Stage 2. `campaign advance STAGE` resumes the
same verified owned session without a new start. A crash during creation permits
lookup of the uniquely named persisted session, never a second creation attempt.

`campaign replay STAGE REVISION` is receipt-only. `campaign ab STAGE PAGE_ID` permits
one additional medium request only for the last completed low request, with one
bounded semantic decision (at most 2,400 estimated input / 512 answer tokens).
It uses the existing session budget, records both receipts separately and performs
a labelled in-memory counterfactual replay. It does not change or resume the stopped
live session. Sizing, truncation, missing contracts and unverifiable/provider failures
are ineligible. Receipt replay cannot reset this allowance.

If preflight fails before a session or manifest exists, `campaign archive-preflight
CAPTURED_ERROR_FILE CAPTURED_BINARY_HASHES_FILE` archives the already observed failure
without a provider call. Capture the attempted binaries before rebuilding audit code.
The exception and endpoint remain encrypted; the report contains only a failure code,
location and fingerprint. The resulting campaign refuses all further dispatches,
including a repeated freeze. It cannot create or recover a planning session.

Campaign state and fixture inputs use encrypted `agent-planning-progressive-*-v5`
records under tenant `planner-progressive`. The isolated EF index is workspace-resolved
`.GnOuGo/data/planner-progressive/schema5-local-metadata-20260912/gnougo-planning-v5.db`.
The earlier database remains untouched. The historical export copies
only immutable benchmark inputs; no historical session, reservation or budget is
migrated. The saved classifier's public caller default (100) becomes the standalone
classifier's optional public threshold; its original callee required that argument.

`campaign report` contains redacted counts, identities and diagnostics. Engine decision
counts cover active executable holes with persisted deterministic resolution origins;
other engine decisions remain unknown. Model page decisions and executable-hole
exposures are separate measures. Planning, independent execution and the optional
diagnostic are attributed separately. `describe` and `inspect` contain private fixture
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
campaign required three independent successful sessions; the progressive campaign
authorizes only the three gated starts above. A preparation checkpoint alone is not a
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
