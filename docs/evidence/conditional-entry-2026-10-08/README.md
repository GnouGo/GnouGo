# Conditional entry requirements

The [retained proposal and counterexample](../indexed-projections-2026-10-08/review-rejection.json) entered an optional-action branch without establishing its action's `requires`. The branch also performed a subsequent observation, so correcting its entry requires an explicit revision. The rejected artifact, receipts and exhausted planning session remain unchanged.

TaskPlan preflight now checks entry-known requirement conjuncts against enclosing branch facts and authoritative contracts. Diagnostics identify the controlling condition, affected operation, requirement and scope. Values first observed inside the branch still use runtime assertions. A nullable boolean unequal to `true` is not assumed to equal `false`; unrelated flags, producers and loop items establish no relationship. Captures, declared exports and group arguments preserve only explicit identities. No runtime, mapping, permission, token-limit or lowering-profile change is included.

Automatic repair receives an exact condition replacement only for an isolated optional action. Its original condition and every `requires` remain intact. Shared operations, inference, active alternatives, cleanup and contradictions require explicit revision. Issued historical schemas and authorities remain unchanged. These checks establish the supported predicate implication, not arbitrary business completeness.

## Reproduction and validation

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
env Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~PlanningRecoveryTests|FullyQualifiedName~RequirementReview'
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability replay-compile --workspace /path/to/workspace --campaign schema-portability-20261002 --run indexprojection20261008a-amazon-1
```

The last command is read-only: it compiles the original retained plan using its retained contracts, without inference, modifying the session or executing external work. Current verification results and fresh live evidence are recorded separately alongside this report. The historical 33/33 benchmark remains unchanged.

## Fresh live boundary

Only one fresh Amazon validation is authorized, with new identities, the existing ten-product maximum, unchanged oracles and the shared EUR 150 campaign ceiling. Pricing/currency readiness and focused deterministic execution must pass first. Concrete revision/hash-bound artifact approval and requirement acknowledgments remain required. Unknown reservations remain retained; uncertain invocations are never replayed. No code-review run or cohort expansion is included. PR #117 remains draft pending its full execution gates.
