# Fresh live validation, 4 October 2026

**The fresh execution gate remains 0/6.** Provider-backed planning ran for all six
slots of `receiptslive20261004b`, but none produced an artifact eligible for execution.
Three Amazon sessions stopped at contract validation. Three code-review sessions
compiled, then inspection found unmet requirements; no approval was submitted.
Consequently this cohort does **not** establish live cookie handling, product visits,
XLSX correctness, Copilot receipt completion, final review output or cleanup.
PR #117 remains draft. Deterministic execution evidence is reported separately below.

## Candidates and bounded corrections

The starting commit was `0a50e4937c3ffbd7bb6616a940d873f70256fb09`, with all
non-skipped CI checks passing. The architecture, `mapping.dynamic`, production
permissions and execution oracles are unchanged.

- `688ab5e8`: the live harness accepts `--review-command <file>` using the existing
  approval command, exact revision/hash and explicit requirement acknowledgments.
  Inspection exposes requirements beside the actual TaskPlan. Started executions
  cannot be replayed; acknowledgments are never inferred from a requirement list.
- `5ac1d8f2`: a fresh Amazon proposal reproduced a generic compiler crash. Invalid
  scope exports prevented creation of a whole-result port, while `present(scope)`
  still indexed it. Presence now uses the same blocked-reference check as a whole
  output. Five parameterized regressions failed before the fix and pass afterward.
  Replaying the retained proposal without inference now returns its original binding
  and export diagnostics instead of `KeyNotFoundException`. It does not accept the
  invalid plan or consume a repair to fix compiler plumbing.
- `17def202b6a51140356d90904039469f3af2f121`: the frozen candidate for cohort **b**.
  The harness records an explicitly authorized campaign ceiling extension through
  the existing encrypted journal and lease. Configuration, historical manifests,
  paid usage and unknown reservations remain intact. No runtime ceiling changed.

The preliminary [cohort a](evidence/fresh-live-2026-10-04/receiptslive20261004a.json)
retains its Amazon compiler failure: two planning calls/attempts, zero repairs,
18,519 input and 8,496 output tokens, €0.309555 and 105.476 seconds. Its five
unattempted slots remain incomplete. Results are not pooled across candidates.

## Fresh cohort b

The [unchanged harness report and manifest](evidence/fresh-live-2026-10-04/receiptslive20261004b.json)
pin source/harness SHA, prompt hashes, environment, provider configuration and limits.
Model: `gpt-5.5-2026-04-24`, medium reasoning, 96,000 input tokens, 32,768 output
tokens, eight physical planning attempts and two repairs. The prompts, pinned
SmartGuide head/base and `real-workflows-v1` oracles are unchanged.

All IDs below have the prefix `receiptslive20261004b-`.

| Run | Planning result / review finding | Calls / attempts | Repairs | Discovery reads | Input / output tokens | Cost EUR | Planning seconds |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| amazon-1 | Nullable consent selector rejected; repair made no progress | 3 / 5 | 1 | 2 | 21,466 / 6,791 | 0.277114 | 707.811 |
| amazon-2 | Nullable workbook path incompatible with accepted required output | 3 / 4 | 1 | 2 | 22,962 / 7,587 | 0.305051 | 397.422 |
| amazon-3 | Same output-contract mismatch | 3 / 3 | 1 | 2 | 24,958 / 9,038 | 0.352722 | 113.782 |
| code-1 | Compiled; report preservation missing on verified failure path | 2 / 4 | 0 | 3 | 33,179 / 3,508 | 0.241546 | 654.700 |
| code-2 | Compiled; report preservation missing and actual review input not bound | 3 / 3 | 1 | 3 | 38,876 / 4,425 | 0.291430 | 60.697 |
| code-3 | Compiled; report writing missing on verified failure path | 2 / 2 | 0 | 3 | 32,677 / 6,035 | 0.306846 | 74.767 |

Generation total: **16 logical calls, 21 physical attempts, four repairs, 15 discovery
reads, €1.774708**. Usage is verified for every new completed request. Five HTTP 500
responses were handled by the existing bounded transport policy. No provider schema
rejection occurred. Runtime inference calls, workflow executions and approvals: **zero**.
Execution latency is unmeasured, not a zero-duration successful execution.

The nullable selector remains invalid: task-envelope presence does not establish that
an extracted field is non-null. The workbook-path contract is also genuinely nullable.
The accepted output interface cannot be silently loosened to admit it; its revision
gate is intentional. No validator relaxation or scenario-specific rule was introduced.

The [review findings](evidence/fresh-live-2026-10-04/review-findings.json) retain exact
artifact hashes/revisions. In code runs 1 and 2, copying a report out of the clone
depends on successful Copilot completion. In run 3, report assembly and writing follow
both Copilot calls. In all three, a verified terminal MCP failure skips those normal
steps and reaches unconditional cleanup. They therefore fail the accepted requirement
to preserve a report before cleanup on failure. Run 2 additionally names `reviewText`
inside a literal prompt without passing its runtime value. Recommendations, compiled
YAML and operation objectives do not repair these missing data/control dependencies.

These are retained invalid proposals and review gaps, not evidence that the Copilot
receipt correction failed. No Copilot session or external checkout was started for
this cohort. The historical uncertain invocation was neither reconciled nor replayed.
Fresh revisions must close the gaps before explicit human approval and execution.

## Budget and retained evidence

The user authorized **an additional €50**, recorded as a ceiling extension from €50
to **€100** in the same `schema-portability-20261002` campaign. The
[authorization record](evidence/fresh-live-2026-10-04/budget-authorization.json)
preserves the prior accounting snapshot. This is not a reset or new campaign.

At collection end:

- Verified spending: **€49.415438**.
- Historical unknown-completion reservation: **€1.303376**, unchanged (one attempt).
- Committed/reserved upper bound: **€50.718815 / €100**.
- Available under that ceiling: **€49.281185**.
- Added cost across cohorts a and b: **€2.084263**. No new uncertain attempt remains.

Collection stopped on validation/review failures, not budget exhaustion. More budget
does not make an uncovered requirement approvable. Raw requests, receipts and proposals
remain in encrypted campaign records; published evidence contains aggregate accounting,
hashes and sanitized diagnostics only. All previous cohorts and the historical 33/33
benchmark remain unchanged.

## Deterministic validation and readiness

[Validation counts and log/oracle hashes](evidence/fresh-live-2026-10-04/validation.json):

- Full solution with `-m:1 -warnaserror`: **4,291 passed, zero failed, 12 expected
  skips** (seven Windows-only and five opt-in paid tests). It completed before the
  extra budget-extension test was added; that test passed in the separate 54-case
  campaign/approval/accounting run. Counts overlap and must not be added together.
- Focused Server run: **67 passed**, including the real local product/XLSX fixture.
  Browser observations: **1 composite test passed**; requirement review: **8 passed**;
  Copilot completion receipts: **10 passed**; compiler semantic validation: **17 passed**.
- Eight harness approval cases cover explicit approval, absent/duplicate/unknown IDs,
  stale revision/hash, non-approval commands and already-started execution rejection.
- Release Planning package and Native AOT planning smoke passed. Full solution
  validation included frontend production/browser checks. No frontend source changed.
- Fresh real Browser/Document readiness verified disposable local navigation, cleanup
  and independently read XLSX cells without a provider. Code readiness verified current
  MCP discovery, pinned commits, Node 24.20.0, Python 3.11.13 and repository-selected
  pnpm 10.34.5. These are readiness checks, not external workflow success.

## Reproduction

Use the frozen source above for reproduction; evidence-only commits do not change which
candidate generated the retained artifacts. Do not execute the three rejected artifacts.
Start any subsequent collection with unused IDs and a new frozen manifest; keep this
cohort's failures in its denominator. See the [harness instructions](../tests/GnOuGo.Agent.Planning.Benchmark/README.md#authorized-schema-portability-live-campaign)
for preparation and explicit review submission.

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipClientBuild=true
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability report --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort receiptslive20261004b
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run receiptslive20261004a-amazon-1
```

Private `inspect-run` output includes accepted requirements, actual tasks, original
responses, revision and artifact hash. Review loop bodies and data dependencies before
writing any approval command. A successful compilation never substitutes for review
or the independent execution oracle.
