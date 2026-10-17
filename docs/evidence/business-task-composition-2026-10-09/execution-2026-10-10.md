# Authorized execution: business-task composition

The user authorized the reviewed artifact with “go for live execution.” The
existing approval command acknowledged all seven requirements at revision 8 and
artifact `47248097b19347e131a9c1e98483ebe281f86df726a7d48f538784237d60f0e4`.
Approval advanced the stored session to revision 9. The reviewed TaskPlan, YAML
and source candidate `1022ed33437179386eb8a43c5d9f3cbf11453bfd` were unchanged.
The frozen binaries, configuration and oracle fingerprints were verified before
dispatch. Pricing/currency readiness passed with no inference.

## Result

`businesstasks20261009a-amazon-1` executed once on October 10, 2026, around 02:52
Paris time. **Execution and the independent E2E oracle failed.** The business
failure is retained even though the harness process returned exit code zero.

| Stage | Observed result |
| --- | --- |
| Planning | Previously reached review: five calls, three explicit revisions, zero repairs, two discovery reads |
| URL derivation | One completed runtime inference, 111 input / 113 output tokens |
| Browser acquisition | Succeeded: 54 complete pages, 1,553 records, no capture/manifest truncation |
| Navigation recovery | Two acquisition attempts; the first generation was discarded after navigation |
| Deterministic guard plumbing | Failed with `CONTRACT_UNSATISFIED`, exhausted `materialized_memory` |
| Learned mapping and product selection | Not reached; zero mapping inference calls |
| Individual product visits / Document writes | Zero / zero |
| Excel oracle | Failed: workbook missing |
| Browser cleanup | Succeeded; the independent current-page check confirmed no active page |

The prior Browser timeout did not recur in this attempt. The first observation
is 1,128,860 bytes when serialized as compact, unescaped UTF-8 JSON; this is not
a provider-request size or a measurement of .NET's escaped serialization.
The last verified HTTP response was GET/200, but the final document generation
has a null HTTP status. These facts remain distinct in the retained receipt.

## Generic failure identified

The failed step is compiler-generated `set` `n_d8d43d374acc492d`, described as
“Check and assemble consecutive deterministic values.” Its first intermediate
binding, `v0`, renames a complete switch-result envelope before selecting a
boolean guard. The renamed envelope carries observation data unnecessary for
that boolean. Failure occurs before the later page-array selection and before
`mapping.dynamic` executes.

The saved error reports:

- 58,751,376 cumulative materialized bytes and 9,934,784 output bytes;
- 76,960,056 allocated bytes, 35 statements and 61.3046 ms;
- outer expression allowance: 1,000,000,000 bytes;
- actual nested sandbox ceiling: **50,000,000 bytes**.

`ExpressionEvaluator.Mapping` applies the existing stricter 50 MB cap. The
nested sandbox checks that cap but reports the shared outer allowance's
snapshot, explaining why the displayed 1 GB ceiling appears unexhausted.
The failure is unrelated to token admission, missing observations, consent,
learned-script specialization or the campaign budget.

The next minimal correction should resolve the required physical output path
before renaming/materializing a whole result envelope, preserving guard order
and every contract check. Resource diagnostics should identify both the shared
allowance and the stricter limit actually enforced. Neither limit should rise.
This execution-only turn changes no production source or approved artifact.

### Zero-inference reproduction

A standalone read-only probe loaded the encrypted journal through
`EncryptedWorkflowRunStore.ReadAsync`, using the frozen candidate's assemblies.
It evaluated the [exact compiled expression](failed-compiled-expression.txt)
against the first finalizer's saved `DataBefore`, with the run's unchanged
expression limits. It did not invoke the workflow engine, a model or an MCP.

The [replay](expression-replay-2026-10-10.json) reproduces the same `v0` error,
58,751,376 materialized bytes and 9,934,784 output bytes. The switch envelope is
9,934,718 bytes in .NET JSON and contains **eight complete copies of the same
snapshot**. Its surrounding context is 19,870,283 bytes. This is observed
amplification in the saved state, not an inference from the memory counter.

The persisted run is `failed`, with 16 normal steps, one finalization step and
`FinalizationCompleted=true`. All external invocations have committed completion
receipts. No execution restart or reconciliation was performed.

For a read-only reproduction in a configured workspace, extract the named set's
input from the unchanged YAML, remove only its `${` / `}` wrapper, and use:

```csharp
var run = await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: workspace)
    .ReadAsync("benchmark", "businesstasks20261009a-amazon-1");
var context = run!.Invocations.Values.Where(i => i.IsFinalization)
    .OrderBy(i => i.PreparedAt).First().DataBefore;
var evaluator = new ExpressionEvaluator(null, run.Limits.MaxExpressionStatements,
    TimeSpan.FromSeconds(run.Limits.ExpressionTimeoutSeconds),
    run.Limits.ExpressionMemoryLimitBytes);
evaluator.Evaluate(expression, context); // Expected CONTRACT_UNSATISFIED at v0.
```

## Accounting and retained state

Execution lasted 29,927 ms, including the harness's oracle work. Its single
runtime inference cost **EUR 0.0035007542816576448664477771**. Previously recorded
planning cost remains EUR 0.7822122637323631200638920934; planning was not repeated.

The campaign upper bound is **EUR 121.58237201376857425569873804 / 150**.
The EUR 6.5016147947905255187922165013 unknown reservation remains unchanged.
There are 931 logical reservations and 928 completion receipts; the three
historical uncertain logical requests retain their existing inconclusive
closures. Five historical unknown physical attempts remain reserved. This run
introduced no new uncertainty or unresolved campaign blocker.

Full observations, model receipts, approval history and the failed execution
remain in encrypted workspace records. The checked-in evidence contains only
bounded metadata and diagnostics. No historical invocation was replayed and no
second live attempt was dispatched. PR #117 remains draft.

## Validation status

The previously completed local solution result remains 5,015 passed, zero failed
and 13 skipped. No source changed for execution, so those tests were not repeated.
The frozen source candidate's deterministic planner CI has now passed. Its build
still has the retained Docker Hub and Copilot checksum-download failures.

The later documentation-head CI has separate failures: one Copilot fixture's
directory disposal raised `IOException: Directory not empty`, and six retained
host fixtures exceeded their existing cumulative runtime elapsed budgets before
mapping inference. No assertions or limits were changed. CI and live acceptance
are not reported as green.
