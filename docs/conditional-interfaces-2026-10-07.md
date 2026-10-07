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

Focused validation: **1,059 planner tests passed**, **four targeted local Browser/Document cases passed**, and the **Release planning package**, **Native AOT planning smoke** and **skill validation** passed. The full solution completed across 33 assemblies with **4,679 passed, one failed, twelve existing skips**. The failure was an `HttpListener` address-in-use error during disposal of the unchanged Browser fixture; its entire **67-test suite passed unchanged on rerun**. All 680 host tests passed. The original failure remains recorded; this is not reported as a clean first-pass solution run. See [validation counts](evidence/conditional-interfaces-2026-10-07/validation.json).

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

One fresh Amazon run executed on frozen candidate `ee24b879`, with ten products maximum and the existing €150 campaign ceiling. Planning reached review without conditional type errors after six calls, one automatic repair and three explicit review revisions. The concrete revision-9 artifact was reviewed and approved under the user's authorization for this validation.

**Execution failed.** Initial extraction/interpretation selected an observed footer “Cookies” information link as consent. After navigation to that information page, mapping generation and its single repair used the wrong source path and failed with `CONTRACT_UNSATISFIED`. No product was visited and no XLSX was written. Both complete Browser acquisitions succeeded without invalidation; workflow cleanup succeeded and the independent check found no active page. Source grounding and type validation did not establish that the observed link was the correct business action.

Planning consumed 88,877 input / 37,342 output tokens and €1.388451 in 480,739 ms. Execution consumed 46,688 input / 6,017 output tokens and €0.367335 in 108,545 ms. Campaign upper bound: **€96.083184 / €150**, including **€2.606753** of unchanged historical unknown reservations. There is no new unknown usage. The six-slot report remains **0/6**, with one failed execution and five unstarted slots.

See [concrete review](evidence/conditional-interfaces-2026-10-07/amazon-review.md), [live report and reproduction commands](evidence/conditional-interfaces-2026-10-07/amazon-result.md), [sanitized execution evidence](evidence/conditional-interfaces-2026-10-07/amazon-execution.json) and [frozen manifest/accounting](evidence/conditional-interfaces-2026-10-07/cohort-report.json). A read-only synthetic replay reproduces the invalid mapping path without inference; it does not modify runtime or the saved workflow. All non-skipped CI checks passed for the implementation commit; evidence-only delivery has its own checks.

The historical rejected workflow and uncertain invocations remain untouched. No code-review evaluation, cohort expansion or second execution is included. PR #117 remains draft; conditional typing regressions passing does not establish live execution acceptance.
