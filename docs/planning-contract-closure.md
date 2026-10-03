# Contract closure before mapping generation

PR #117 retains `LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML`.
TaskPlan still contains business operations, arguments, dependencies and structured scopes.
There is no additional planning call, representation, executor or mandatory MCP metadata.

## Compilation boundary

`TaskPlanCompiler` no longer stores `Bound.Expression` or emits JavaScript for predicates,
JSON encoding or cleanup guards. The existing `PlanningValue` carries `predicate`
(a closed operator name in `Text`, operands in `Items`) and `json` (one operand).
These forms survive serialization and validation. Only final `PlanningGraphCompiler`
lowering emits expressions, including executor envelopes and runtime addresses.
Direct bindings, object/array assembly and checked projections retain their native forms.
A source-less input binding forwards the accepted input object intact, so confirmation
does not materialize omitted optional inputs or turn them into nulls. It grants no artifact provenance.

Existing validation now checks complete operation requests, local-workflow arguments
and every exported value before lowering. This includes conditional JSON Schema
requirements, catalog-owned bindings, types, availability and artifact provenance.
Two retained regressions previously passed graph validation and failed during YAML
validation; they now fail before mapping with `CONTRACT_UNSATISFIED`.

Terminal business dependency failures use the same public code, preserving the original
reason in `ValidationStage` and the diagnostic's task/input location. Bounded scoped
repair still uses the original semantic diagnostics and authority. Compiler-generated
plumbing failures remain `TASK_COMPILER_VALIDATION`; no model is asked to repair them.

## Defaults and opaque results

Only explicit authoritative defaults fill absent required operation arguments or
required members of supplied objects, including literal array items. Defaults are
validated before use. Optional omitted arguments stay omitted; explicit null and
invalid supplied values are never replaced. Accepted public-input defaults remain
owned by requirements. Plans and discovered contracts are not mutated to add defaults.

An opaque whole result can satisfy a typed consumer only through an emitted
`value.project` with `paths: [[]]` and the consumer's exact schema. It executes before
the consumer and publishes no result on failure. An opaque pass-through remains opaque
inside `{ value: … }`; wrapping does not declare business fields. Existing MCP transport
decoding preserves structured content, parses a single valid JSON text block and retains
plain or malformed text and mixed protocol payloads. A typed consumer rejects incompatible
data rather than treating a parse failure as null.

Free-text extraction remains an explicit business `transform`, with an approved runtime
inference call and closed result contract. The compiler does not silently enable MCP
structured-output inference. Neither normalization nor extraction establishes artifact
ownership, permissions or factual correctness. Independent execution oracles still reject
well-typed fabricated values.

## Compatibility and validation

TaskPlan and storage format 10 are unchanged. The additive graph value kinds reuse
existing fields; historical expression graphs and exact confirmation wrappers remain
readable. Saved YAML, issued requests, receipts and approvals are not rewritten.
Rebuild the planning package and host together; regenerate changed workflows and obtain
fresh approval through existing contract/artifact fingerprints.

Baseline: 108 focused planner tests and 13 local execution cases passed before changes.
The updated planner suite contains 813 tests, run with warnings treated as errors. Coverage
includes script-free business graphs, immutable lowering, defaults, opaque normalization,
short-circuit cleanup, refusal, artifact provenance and early complete-request rejection.

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~BusinessTaskPlanExecutionTests|FullyQualifiedName~LocalProductOutcomeExecutionTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
```

## Deterministic comparison

The unchanged existing harness ran five cases with three measured repetitions after
one warm-up per case in isolated baseline and candidate builds. Both passed **15/15**
execution oracles; each run used **one planning call, zero repairs and one discovery
read**. The response schema stayed at **16,863 bytes**. Median estimated input tokens
changed from **7,281 to 7,450** because the prompt now states closure/default ownership.
Planning median/p95 changed from **29.06/34.10 ms to 30.64/37.99 ms**; execution
median/p95 changed from **2.82/8.94 ms to 3.00/10.43 ms**. These short local timings are
observations on a shared local host, not a provider latency claim. Usage is estimated, not billed usage.

[Measurements and unchanged harness command](evidence/planning-contract-closure/deterministic-comparison.json)
are separate from all historical benchmarks and live results.

## Live evaluation

The authorized follow-up resumes `schema-portability-20261002`, sharing its original
EUR 50 ceiling across planning, runtime extraction, Copilot inference and diagnostics.
The inspected pre-change ledger records EUR 17.7346857074256 and no unknown transport
reservations. The retained original rejection identity is not dispatched again.

Use the existing [campaign commands](../tests/GnOuGo.Agent.Planning.Benchmark/README.md#authorized-schema-portability-live-campaign)
with a fresh cohort and run identities after freezing the candidate. Pin the same
toolchain PATH for readiness, planning and execution. Require all three Amazon and
three code execution oracles; retained failures and external blocks remain failures
or incomplete coverage. Review output stays local. Historical 33/33 results and
earlier failed live cohorts remain unchanged and do not validate this correction.

## Retained live iteration

Candidate `f58f16259532bd0d3be4a59734a5c8612d92349b` passed 4,107 solution tests
with zero failures and 12 platform/opt-in skips. Its first Amazon run stopped after
four calls and two repairs. The saved proposal combined an invalid nullable selector
with a generic compiler defect: field selection rejected object unions even when
every conditional branch declared the field. A model-free reproduction confirmed
the defect. Field selection now preserves the union of the declared field contracts;
missing, opaque and scalar alternatives remain rejected. The original nullable
selector still fails. Saved proposals are not rewritten.

The first code proposal reached review in four calls and zero repairs. Execution was
withheld: a runtime prompt-building transform referred to earlier planning constraints
without receiving them. Its bindings omitted fixed report/evaluation instructions.
Prompt guidance now makes this data boundary explicit and requests deterministic
value/object/JSON wiring; there is no new semantic-proof mechanism. Artifact review
and independent execution oracles remain required.

[Retained failure and accounting](evidence/planning-contract-closure/retained-union-failure.json).
The incomplete `closure1` cohort is preserved separately from subsequent candidates.
