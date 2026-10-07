# Explicit conditional interfaces

The retained `snapshotlive20261007a-amazon-1` proposal forwarded two differently shaped objects through one scope output. Their union could not supply a concrete boolean condition or string argument downstream. The compiler correctly rejected it before execution. Forwarding a path or opaque object is not evidence that the downstream operation can consume it.

Fresh generation guidance now prefers the same explicit consumer-facing ports in each alternative, with matching types, nullability and requiredness. Each branch projects its own observed values. For example, export `ready` and `reference` separately instead of forwarding unrelated whole result objects. Keep `reference` nullable when the producer allows null. Place its non-null consumer on a branch guarded by `reference != null`; `ready` alone establishes no relationship to that reference.

This is guidance, not automatic schema rewriting. Compatible object exports and opaque pass-through still work. Nothing flattens objects, invents missing observations, widens unions, changes compiler acceptance or expands repair permissions. An interface change outside issued repair slots needs explicit revision.

Existing type diagnostics now include the traceable conditional branch/export paths and their inferred contracts, including object field requiredness. The context follows existing value and sequence exports. Diagnostic codes and locations remain unchanged. No execution, mapping or permission code changed.

## Deterministic evidence

- [Rejected structural reproduction](../tests/GnOuGo.Flow.Planning.Tests/Fixtures/ConditionalInterfaces/differing-objects.json) and [corrected explicit ports](../tests/GnOuGo.Flow.Planning.Tests/Fixtures/ConditionalInterfaces/explicit-ports.json), reduced from the retained proposal without live data. Tests rename tasks, operations and ports.
- [Conditional interface tests](../tests/GnOuGo.Flow.Planning.Tests/ConditionalInterfaceTests.cs) cover both alternatives, nested scopes/captures, exact values and nulls, one-call/zero-repair review, unchanged serialized recovery, rejected interface restructuring through scoped repair, invalid guards, missing exports, incompatible types and invented fields. Compatible whole objects and opaque pass-through remain supported.
- [Local Browser/Document execution](../tests/GnOuGo.Agent.Server.Tests/LocalProductOutcomeExecutionTests.cs) uses actual MCP processes, consent present/absent, complete snapshots and direct branch ports. Independent assertions retain product visits, XLSX cells and cleanup. Missing observed controls remain null.

The first broader planner run caught a request-size regression from longer guidance (24,043 estimated tokens against a 24,000 limit). Wording was shortened; neither the ceiling nor the retained discovery test was changed. The separately observed schema-validation CPU issue is outside this correction.

Focused validation: **1,059 planner tests passed**, **four targeted local Browser/Document cases passed**, and the **Release planning package**, **Native AOT planning smoke** and **skill validation** passed. Full-solution results and live evidence follow after collection.

Validation commands:

```bash
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests&DisplayName~complete'
PLAYWRIGHT_MODULE_PATH="$PWD/src/GnOuGo.Browser.Mcp/bin/Debug/net10.0/.playwright/package/index.mjs" \
  dotnet test GnOuGo.Agent.sln -m:1 -warnaserror \
  -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

The model-metadata and frontend build flags avoid unrelated generation; no UI changes are included. Native smoke inference is deterministic. Skill validation uses `skill-creator/scripts/quick_validate.py` with PyYAML available.

## Live validation

One new Amazon run is authorized after deterministic gates, with ten products maximum and the existing €150 campaign ceiling. The historical rejected workflow and uncertain invocations remain untouched. New generation and execution results, concrete artifact review, accounting and unchanged execution-oracle evidence are recorded separately below when available. No code-review evaluation or cohort expansion is included; PR #117 remains draft.
