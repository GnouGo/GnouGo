# Contract-aware generation and compositional outcomes

> Historical implementation evidence. The outcome annotations and repair versions described below are superseded by [business TaskPlan simplification](planner-simplification.md). Measurements and failed cohorts remain unchanged.

This correction covers Copilot session `4ad7dd6c82af4bd3924ea25a3edc44cd` and product-search session `fba8eeb59c3240c8b147267abff64945`. Sanitized responses, issued requests, diagnostics and repair history are retained in `tests/GnOuGo.Flow.Planning.Tests/Fixtures/ContractAwarePlanning`. Neither saved workflow is modified or rerun.

The Copilot proposal bound prose to an evidence array and used a runtime clone result as the approved workspace, although creation already specified a fixed destination. Two repairs missed both bindings. A numeric schema-reader defect also rejected integral `minLength: 1`. The recorded runner supports files, not command execution: correcting types and scope cannot authorize installation/testing.

The product proposal compiled, but version-1 outcome validation rejected supporting interactions, transforms, a foreach container and reported outputs. Browser/Document effects were also absent from discovery. Both the producer contracts and compositional validation needed correction.

## Contracts and generation

Producers publish `_meta.gnougo.effect = { "version": 1, "kind": "read" }` through existing MCP metadata. Kinds are `none`, `read`, `write`, `execute` and `lifecycle`. Flow.Integrations validates the declaration and maps it to `EffectKind`. Absent metadata retains the read-only annotation fallback, otherwise `unknown`. Malformed declarations and mutating effects contradicting a true read-only hint fail discovery. Metadata grants no permissions.

Browser content/wait/screenshot declare reads; interactions conservatively declare execution; closure declares lifecycle. Document policy/list/read declare reads and document writing declares write. Changed contract fingerprints require refreshed discovery and approval.

New prompts and selectable response operations share the admitted exact contracts. Index-only entries remain discoverable but cannot be selected without inspection. Initial injected contracts, explicit inspections, cached receipts, pending requests and cumulative budgets remain intact.

Literal bindings expose authoritative scalar, nested array/object and enum shapes with supported constraints. Prose and encoded JSON cannot satisfy structured contracts. Definitions are factored to respect strict schema nesting without losing constraints. Complete semantic validation still checks required members, duplicate members, uniqueness, references, cross-field rules and constraints unsupported by the strict response profile. Integer-valued schema keywords accept integral JSON numbers independently of their internal numeric representation, while rejecting prohibited fractional, negative and out-of-range values.

Agent scope remains literal. Fixed creation arguments become read-only repair context so creation, workspace and cleanup can reuse one destination. Requested installation/testing remains an execution outcome. If runner capabilities/evidence are insufficient, the existing loop discovers compatible alternatives and asks for a recommended or custom choice; without a compatible alternative it must report the limitation. Answers cannot grant permissions or approve artifacts.

## Outcome version 2 and repair version 5

Bindings describe a supporting computation within the existing TaskPlan. Resolved, policy-allowed operations witness the accepted effect. Connected prerequisites, consumers, transforms and structural containers may contribute. Dependencies follow consumed group inputs, captured values and enclosing loop/branch controls. Unused arguments and unreachable calls cannot lend support. A shared predecessor alone does not make an unrelated sibling relevant. Root outputs may report results but never witness external work.

Traversal covers invoked groups, branches, sequential/parallel loops and cleanup. `coverage: "once"` (also the absent-field meaning) requires an invocation on every required path. `coverage: "each_item"` binds an existing `forEachTaskId` and requires a witness on every body path. Empty collections satisfy per-item coverage without claiming an invocation. Conditional and cleanup guarantees stay explicit. Missing effects produce `OUTCOME_EFFECT_UNDECLARED` instead of misclassifying real operations as values.

Version-5 repair permits only diagnosed mapping corrections to existing tasks, outputs and foreach scopes. Accepted objectives, effects, coverage and unrelated mappings remain immutable. Context does not grant edit authority. Patches apply to clones and undergo full compiler/outcome validation; duplicate, unrelated and invalid edits fail atomically. Complete authority is fingerprinted. Generated graph defects remain compiler failures.

TaskPlan, PlanningGraph and storage format 10 stay unchanged. Coverage and foreach references are optional review metadata, omitted when absent. Historical version-1 semantics, earlier repair envelopes, issued schemas and approvals remain unchanged. Fresh sessions and explicit revisions use the new semantics. Static support cannot establish natural-language equivalence or successful execution.

## Reproducible checks

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~SharedWorkspaceExecutionTests|FullyQualifiedName~PlanningSessionLifecycleTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests --no-build \
  --filter FullyQualifiedName~ComparableContractOverhead --logger 'console;verbosity=detailed'
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -warnaserror -o artifacts/contract-aware/planning-aot
artifacts/contract-aware/planning-aot/GnOuGo.Flow.Planning.Smoke
```

The product tests start actual Browser/Document stdio MCP processes and a loopback-only product site. Generated YAML executes through real Flow; deterministic extraction adapters consume observed HTML. Independent OpenXML reads verify exact cells, order and output paths. Variants cover multiple/changed products, empty results, missing fields, navigation failure, denied writes and collection bounds. A post-run browser request verifies cleanup left no active page. Files, processes and server configuration are disposable; no Amazon request occurs.

`SharedWorkspaceExecutionTests` runs a local clone/agent/cleanup slice with actual Git/Cmd components, a disposable repository and a deterministic runner. It covers refusal, verification failure, cancellation, partial creation, missing resources, isolation/budget failures, approved path reuse and unrelated-file preservation. Command observations are adapter evidence, not live Copilot execution. Capability-alternative tests cover recommended/custom answers, preserved installation/testing intent, restart, cumulative budgets and separate approval.

For published producer checks, run the same product tests with `GNOU_GO_BROWSER_MCP_TEST_EXECUTABLE` and `GNOU_GO_DOCUMENT_MCP_TEST_EXECUTABLE` set to published executable paths. Browser stays managed because of Playwright; Document and planning use supported Native AOT targets. Linux requires Chromium system dependencies; planner CI installs them explicitly. Published encrypted recovery uses the existing [persistence smoke](planning-clarification.md).

## Evidence and limits

| Retained provider run | Calls | Repairs | Input / output tokens | Active planning time | Execution |
| --- | ---: | ---: | ---: | ---: | --- |
| Copilot | 6 | 2 | 63,646 / 14,909 | 252.82 s | Not reached |
| Product search | 2 | 0 | 19,058 / 5,676 | 90.95 s | Not reached |

These historical counts are not a matched baseline for deterministic execution. The new product fixtures use one planning adapter call and zero repairs. The contract-cost comparison interleaves three repetitions of outcome-v1 and outcome-v2 on one build with the same simple execution oracle, reporting conservative input estimates and host planning/execution latency. Missing provider usage remains unknown; these measurements do not establish live provider speed or reliability.

Final sanitized measurements and validation are retained in `docs/evidence/contract-aware-planning/`. The historical 33/33 campaign is unchanged and does not cover these failures. No paid evaluation, real-site browsing, external repository execution, host-policy change or automatic reconciliation is included. Real-site availability and the file-only Copilot host limitation remain open.

## Measured correction

The solution run passed **3,986 tests across 33 projects**, with zero failures and 13 existing opt-in/platform skips. This includes 794 planner tests, 553 Agent.Server tests, 85 integration tests, 43 Browser tests, 63 Document tests and 42 MCP-core tests. Release packages, planning Native AOT, Document Native AOT, managed Browser publishing, the frontend build and published encrypted recovery pass. The seven product variants pass on development and published components; eight local agent lifecycle variants pass with deterministic runner evidence.

| Isolated simple fixture | Outcome v1 | Outcome v2 |
| --- | ---: | ---: |
| Execution oracles | 3/3 | 3/3 |
| Planning calls / discovery / repairs | 1 / 0 / 0 | 1 / 0 / 0 |
| Estimated input tokens | 6,270 | 6,677 |
| Planning median / p95 | 6.47 / 6.63 ms | 17.70 / 19.82 ms |
| Execution median / p95 | 0.60 / 0.60 ms | 0.67 / 0.67 ms |
| Total median / p95 | 7.07 / 7.23 ms | 18.38 / 20.46 ms |

The new contract costs 407 estimated input tokens and about 11 ms of median host planning time in this fixture. Deterministic schema construction/factoring adds work; no universal call or latency reduction is claimed. The retained 74-operation catalog still fits its original 24,000-token ceiling after explicit inspections.

Published execution initially exposed a test-configuration gap: Browser's `KeepBrowserOpen` setting prevents closure. Tests now pin it false inside disposable subprocess configuration and assert there is no active page after cleanup. The production/host setting remains unchanged. Retained [iteration findings](evidence/contract-aware-planning/iterations.json), [comparison samples](evidence/contract-aware-planning/comparison.json), [execution observations](evidence/contract-aware-planning/execution.json), [validation](evidence/contract-aware-planning/validation.json) and [manifest](evidence/contract-aware-planning/manifest.json) distinguish historical failures, deterministic adapters and real local component execution.

The final source is `c20750e3`. Six additional regressions cover group arguments, enclosing loop controls, unrelated siblings, unused arguments and unreachable calls. Linux CI exposed inherited Kestrel endpoints in the local-site fixture; configuration isolation fixes it, including a deterministic recheck with two conflicting endpoints. Earlier isolated timing samples remain in [their original cohort](evidence/contract-aware-planning/comparison-73c6338e.json); host timing varies between runs.

On that implementation commit, the [Linux deterministic planner pipeline](https://github.com/GnouGo/GnouGo/actions/runs/36977798575) passes tests, packages, benchmark-host build, Native AOT and the published Git contract. The [stable .NET, Agent.Server and Linux server publication jobs](https://github.com/GnouGo/GnouGo/actions/runs/36977798927) also pass. Unrelated desktop/container matrix jobs are reported separately by CI.
