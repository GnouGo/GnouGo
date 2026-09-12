# Schema-5 offline validation

Validation date: 2026-09-12. Branch: `feat/deterministic-planner-v2`.
The implementation is a working-tree change based on
`2536c277232995d9d9fac06c9fae9cfe058817a0`; no new live session was started.
The retained evidence still contains 23 sessions and zero complete CodeReview successes.

| Completed check | Result |
|---|---|
| `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror` | 2,945 passed across 29 test projects; one opt-in live test skipped. Includes 602 planner tests. |
| `dotnet build GnOuGo.Agent.sln -m:1 -warnaserror` | Passed, zero warnings and errors. |
| Agent.Server `ClientApp`: `corepack pnpm build` | Passed without warnings. |
| Release packages: Flow.Core, Flow.Planning, Flow.Integrations, AI.Core, Agent.Shared | All five built without warnings. |
| Planning.Smoke Native AOT, `osx-arm64` | Published and executed successfully: typed planning and encrypted runtime persistence. |
| Agent.Server trimmed/self-contained/single-file, `osx-arm64` | Published and executed successfully with `--planning-persistence-smoke`: encrypted schema-5 EF Core persistence. |
| Historical evidence audit | 23 unchanged revisions; no provider dispatch. |
| Frozen fixture contract self-check | 18 cases passed; no model or business transport calls. |
| `git diff --check` | Passed. |

The published checks use the repository's existing documented publish-local trim/AOT
exceptions; no blanket library analyzer suppression was added. They validate macOS
ARM64 binaries, not every release platform. Package and binary outputs were retained
under `/tmp/planning-v5-packages`, `/tmp/planning-v5-aot` and
`/tmp/planning-v5-server`; detailed final build/test/publish logs use
`/tmp/planning-v5-verified-*.log`.

The regression corpus covers these boundaries:

| Boundary | Regression coverage |
|---|---|
| Engine-owned facts | `DecisionReferenceTests`, `SourceDecisionTests`: exact spans, foreign/stale references, no quotation or arbitrary offset responses, governing removals and protected host policy |
| Bounded requests | `DecisionBudgetTests`, `DecisionPagingTests`, `ConvergenceInvariantTests`: every phase, enlarged domains, input and answer ceilings, local schema references, complete page merging |
| Capability proof | `CapabilityDecisionPageTests`, `CapabilityEvidenceTests`, `TypedDecisionGroundingTests`: cross-page coverage, declared ownership targets, original artifacts and conditional outcomes |
| Coordinator behavior | `BehaviorRecoveryTests`, `ConvergenceInvariantTests`: owned identities, callee boundaries, producer/caller ordering, exact revision companions and renewed review |
| Corrections | `HoleRepairTests`, `RepairAcceptanceTests`, `DecisionPagingTests`: staged invalid assignments, preserved neighbors, one unchanged-decision correction across gates, monotonicity and reservation restart |
| Phase profiles | `PhaseReasoningTests`: routine low, behavior/semantic medium, persisted effective reasoning, missing or unsupported metadata and partial batch failure |
| Safety and execution | Existing graph, contract, provenance, ownership, finalizer, scenario-pass and exact-hash approval tests remain in the planner suite |
| Persistence and hosts | Agent.Server and Flow.Integrations suites plus published-binary smokes: encrypted schema-5 state, typed outcomes, page/correction lineage, receipts, optimistic revisions, tenant isolation and save conflicts |

Historical inspection used `scripts/planner-schema4-audit.sh --all-retained`.
All 23 source revisions remained unchanged. The inspected checkpoints were stopped
or waiting: there were zero local advances, zero replayed receipts and zero provider
dispatches. This is an evidence-integrity audit, not a replay of the new planner against
23 successful response histories. Historical decoding exists only in the pinned audit
checkout; production reads schema 5.

`scripts/planner-schema4-audit.sh --verify-fixtures` passed all 18 frozen fixture
contract cases, including denied unconfirmed/unconfigured invocations. It used the
encrypted historical catalog in memory, with no model or business transport calls.
The schema-5 benchmark's direct fixture command initially reported that evidence had
not been captured in its fresh namespace; the audit hook supplied the existing frozen
evidence without creating a live session or copying schema-4 data into production.
Fixture contract checks do not execute a newly generated workflow and do not satisfy
the live success criterion.

No live-model cost or convergence improvement is claimed. Separate live authorization
is still required to prove one success, then three independent successes on the same
frozen commit/catalog/policy/budgets, each with all 18 independent execution fixtures
and exact artifact-hash approval. Failed and unverifiable historical attempts remain
in the evidence ledger.
