# Schema-5 business clarification

Clarification is evaluated after capability preparation, then again after available
behavior assembly. The initial `business_choice` interpretation is only a candidate.
It grants no permission to question the user or claim unsupportedness.

The coordinator resolves the complete containing source clause, including text across
interpretation-page boundaries. Known values and declarations remain references;
semantic responses cannot supply quotations, preference authority or contract bodies.
Locked runtime decision/confirmation contracts establish executable conditions directly.
Remaining interpretations and governing relationships use the existing bounded decision
pages, durable receipts, reasoning profiles and correction allowances.

One eligibility calculation enforces required operations, indivisible capabilities,
producer dependencies, other resolved decisions and governing source constraints. It
runs to a fixed point before exposing a question. Answers supersede earlier choices
for their subject; mandatory policy remains binding. Unchanged baseline behavior can
govern a choice. Declared default applicability distinguishes omitted values from
explicit null. Missing applicability proof is a technical stop, never an invented default.

One admissible outcome resolves automatically. Equivalent outcomes require identical
owned behavior evidence and operation assignments; matching descriptions are insufficient.
Different implementations or missing contracts cannot become questions. Runtime
decisions, confirmations, cleanup and resource mechanics are not planning alternatives.
An empty domain produces `Unsupported` only with complete assessment and exclusion
evidence. A model-proposed sample never establishes completeness; missing proof stops
technically. User questions require distinct, admissible business effects.

Questions display two to four suggestions while retaining the assessed domain. A
nonbinding declared preference can mark one suggestion, with an engine-rendered reason
and supporting references. Conflicting preferences mark none. No suggestion is selected
or submitted automatically. The designer and shared API display labels rather than IDs.

Selected IDs remain typed assignments. Free text is staged and assessed against the
same admissible outcomes using a bounded semantic decision; unclear or different
behavior remains unresolved and grants no new execution authority. Users can explicitly
revise intent for new behavior. Repeated custom submissions reuse completed pages.
Answers keep the session, interpretation, discovery, locked contracts, budgets and
receipts. Affected behavior loses approval and must pass review again. Behavior
validation and artifact approval reject operations inconsistent with the selected
outcome. Contract fingerprints include governing decision evidence.

Schema 5 and encrypted namespaces remain unchanged. Additive decision records contain
references, applicability, exclusions, selection origin and replay-safe event identities.
Existing pending questions without eligibility proof are reassessed only after explicit
interaction. Their old answer is not granted authority, and allowances are not reset.
Redacted resolution, exclusion, preference and clarification events use the existing
planning activity, including its tenant context; source text is not logged.

## Offline validation

Implementation starts from `6875491` on `feat/deterministic-planner-v2`.
Both archived campaigns remain unchanged. No live provider requests or sessions were run.

The threshold regression retains the captured classification of **“when omitted.”**
and supplies synthetic responses under the new decision contract. It verifies that
the complete declaration of default **100** reaches resolution and remains in the
behavior input, with no clarification. This is deterministic regression evidence,
not a new successful live receipt.

The CodeReview regression consumes the frozen matching catalog and benchmark policy.
Its interpretation and locked runtime contracts are explicitly synthetic fixtures.
Conditional publication resolves without a model call or clarification; a renamed
implementation behaves identically. A controlled unresolved-business variant produces
one question. These tests do not claim full independent CodeReview execution success.

Additional regressions cover source precedence, omission/null applicability, unknown
proof, empty domains, equivalent outcomes, coupled-choice closure, bounded suggestions,
preferences, same-session typed/custom answers, stale scopes, behavior preservation,
encrypted restart, API/designer rendering and receipt-safe accounting. The published
serialization and EF/KeyVault smokes include the new decision and clarification fields.

Final checks on 2026-09-12:

| Check | Result |
|---|---|
| Solution build, warnings as errors | Passed; zero warnings/errors |
| Full solution tests against the final build | 3,018 passed, one existing opt-in test skipped, zero failures |
| Planner component | 630 passed |
| Agent.Server component | 351 passed |
| Flow.Core component | 841 passed |
| Agent.Server Vite/pnpm build | Passed without warnings |
| Isolated benchmark harness build (no execution) | Passed without warnings |
| Flow.Core, Flow.Planning and Agent.Shared NuGet packages | Built successfully |
| Published macOS ARM64 Native AOT planning/runtime persistence smoke | Passed |
| Published macOS ARM64 trimmed Agent.Server EF/KeyVault persistence smoke | Passed |

The publishes use the repository's existing documented Jint/framework exceptions;
no new warning suppression was added. The isolated server publish skips bundled MCP
tools and skips its frontend build because the frontend was built separately.

```sh
dotnet build GnOuGo.Agent.sln -m:1 -warnaserror
dotnet test GnOuGo.Agent.sln --no-build --no-restore -m:1 -warnaserror
corepack pnpm --dir src/GnOuGo.Agent.Server/ClientApp build
dotnet pack src/GnOuGo.Flow.Core -c Release -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet pack src/GnOuGo.Agent.Shared -c Release -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  -o /tmp/clarification-planning-aot -m:1 -warnaserror
/tmp/clarification-planning-aot/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true \
  -p:PublishTrimmed=true -p:PublishSingleFile=true -p:PublishAot=false \
  -p:SkipBundledServerTools=true -p:SkipClientBuild=true \
  -o /tmp/clarification-agent-trimmed -m:1 -warnaserror
/tmp/clarification-agent-trimmed/GnOuGo.Agent.Server --planning-persistence-smoke \
  /tmp/clarification-agent-persistence
```

Live validation requires separate authorization. These offline results neither restart
an archived campaign nor establish a new end-to-end CodeReview live success.
