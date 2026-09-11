# CodeReview convergence benchmark

This explicit live harness drives `PlanningSessionService` from Agent.Server using
its KeyVault-configured model, encrypted EF Core session store, durable request
journal and normal planner. It has no alternate planning path. Discovery uses the
frozen catalog. Independent execution uses synthetic frozen transport observations
and has no external business transport. The fixtures make no claim about the current
contents or results of the supplied PRs. The harness cannot publish a GitHub review.

`capture` reads a saved session once and writes an immutable encrypted evidence copy
in the `planner-benchmark` tenant. It never advances or changes the source session.
The isolated index defaults to `.GnOuGo/data/planner-benchmark/gnougo-planning-v4.db`,
resolved through `GnOuGoWorkspace`; `PLANNING_BENCHMARK_DATABASE` can override it.
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
never retry an unverifiable dispatch or reset its budget. Three independent successful
sessions are required; a preparation checkpoint alone is not a successful benchmark.
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
