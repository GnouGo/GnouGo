# First-attempt TaskPlan implementation evidence

This is an offline implementation comparison. No paid model inference or externally
effectful live execution was performed. The live improvement gate is **inconclusive**.
Scripted success and smaller schemas cannot establish improved model decisions.

## Frozen material

The initially clean baseline was `676b5acef3442b1436149eaf9581c7d5e99fe1a3` on
`fix/planner-stabilization`. [Baseline identities](baseline.json) record the original
fixture tree and hashes of the contracts, oracles, prompt and schema material.
The planning skill was the first implementation edit; its validator and relative
links passed, and the revised skill was reread before implementation continued.

The candidate is the uncommitted working tree identified by [candidate.json](candidate.json)
and the full [source-file hash manifest](candidate-source-files.json.gz). Production
SHA-256 is `acc3cf3d4bb09541977384cacd1b3a9c1949f00bd59d33d37ccfa5e3cd2760c3`;
the shared benchmark harness SHA-256 is
`be728cbe278d52ae7aa3c9d53e5d6707158110f8d04d1149bc64ed8ec5bce76c`.
The final candidate was frozen after the unreachable-branch, exact-number and explicitly empty-loop
contradiction regressions. Earlier candidates and their passing fixture cohorts
remain separately retained.
Raw result `source_commit` values identify the checkout base, not the uncommitted
candidate; use the sidecar hashes to identify the candidate.

Baseline production was evaluated in an isolated worktree at that commit using the
same candidate measurement harness. Baseline production files were not changed.
The fixture and oracle manifest is identical in both cohorts:
`0b355c8fffc16681545e237f8649beb7742823850b2edb6462d2dd244ed6493f`.
Historical recorded response, approval and YAML fixtures were not rewritten.

## Prompt presentation comparison

Both prompts are generic. This comparison does not replace a scenario-specific
baseline with a generic candidate. [Retained requests before](requests-before.json.gz)
and [after](requests-after.json.gz) come from the existing seven-call scripted
targeted-discovery case. For the presentation-only pair, only the fixed instruction
prefix changes; each baseline context, contract set and schema stays identical.
[The comparison source](compare-presentation.cs) uses the existing conservative
request estimator, not a model tokenizer or provider receipt.

| Seven-request cumulative estimate | Value | Change from baseline |
| --- | ---: | ---: |
| Baseline generic instructions | 140,954 | — |
| Candidate instructions, identical baseline contexts and schemas | 141,065 | +111 |
| Complete candidate presentation and adaptive discovery | 140,987 | +33 |

[Per-call results](presentation-comparison.json) preserve the comparison. This
shows no request-size saving and measures no business-correctness improvement.
These request captures precede the final compiler-only contradiction fixes;
those fixes do not change request presentation.

## Deterministic execution comparison

The eight existing business cases each ran three times through the same measurement
harness. Responses are scripted, integrations are mocked, and execution is checked
against the existing independently authored outcomes and negative variants.

| Measurement | Baseline | Candidate |
| --- | ---: | ---: |
| First proposals structurally valid | 24/24 | 24/24 |
| Unchanged first proposals passing independent execution oracles | 24/24 | 24/24 |
| Planning calls | 24 | 24 |
| Repairs | 0 | 0 |
| Safety violations | 0 | 0 |
| Provider tokens, cost | Unknown | Unknown |
| Live model calls | 0 | 0 |

See [baseline rows](baseline-fixtures.jsonl.gz), [final candidate rows](candidate-fixtures.jsonl.gz)
and [comparison](fixture-comparison.json). Original first proposals, diagnostics,
FinalReview and execution outcomes are distinct measurements. Individual omission,
extra-operation and redundant-task counts remain unknown unless an independent
oracle establishes them; they are not inferred as zero from compilation or approval.

Three additional local-file fixture tests use inspected Cmd MCP contracts and
the shipped stdio executable in disposable workspaces. They execute the existing
13 variants across read, copy and ordered loop/cleanup workflows, including missing
files, repeated and empty selections, disabled execution and permission denial.
Each authored proposal requires one planning call and zero repairs. These are real
local filesystem executions with scripted planning, not live-model results. The
same tests are run against baseline and candidate production. Retained product/code
compositions and their negative variants remain in the affected host suite.

A zero-failure scripted baseline cannot support a relative failure-reduction claim.
The separately authorized live gate requires complete, comparable three-repetition
cohorts, independent first-proposal judgments, known usage, at least 25% fewer
business-incorrect first proposals, no safety regression, and no higher median
calls or tokens. Missing judgments or usage make the gate inconclusive. The harness
reports descriptive uncertainty and never manufactures live review acknowledgments.

## Validation and retained failures

[Validation results](validation.json) identify completed checks and platform limits.
The final serial solution run passed with `-warnaserror`: **6,032 passed, zero failed,
13 skipped across 33 test projects**, including all 1,609 planning tests. The skips
are seven Windows-only tests, five opt-in live-model tests and the optional browser
test run separately below. Vite, Release packages, Planning Native AOT, the supported
trimmed Server publish and published encrypted recovery also passed on macOS arm64.
The [complete serial log](logs/solution-clean.log.gz) and per-project totals are retained.
Compressed logs are retained under `logs/`; browser interaction outcomes are in
[browser-results.json](browser-results.json). The ordinary suite leaves the optional
browser test disabled; it is also run explicitly with Playwright to exercise keyboard,
mobile, custom-answer, requirement-review and recovery interactions.

Regression-first runs exposed missing primitive guidance, legacy fresh transforms,
unbounded fresh guards, repeated optional resolution, missing review details and
attempt-count correction escalation. Bounded assertion tests also exposed false
positives in explicitly unreachable alternatives and signed zero, plus precision
loss between distinct large integers, and an explicitly empty loop body. These defects were fixed before the final
candidate freeze using exact numeric tokens and JSON numeric equality. The original
failed/superseded aggregate run and focused
red runs are retained separately from successful checks. Some intermediate failures
were test authoring or changed fresh-schema expectations; they are not evidence of
live-model reliability.

The longer serial run also found nine stale assertions in two host test classes:
fresh synthetic transforms now carry an explicit mode, and fresh private correction
bookkeeping uses version 6. Those expectations were corrected; all 11 tests in the
owning classes then passed, including historical replay and durable recovery. The
failed serial run remains retained. Historical recording files were not changed.

The first Server publish command omitted the documented non-AOT host profile and
triggered the Files dependency's two pinned EF experimental notices. The supported
trimmed, non-AOT Server profile is validated separately; no warning suppression was
added. Planning Native AOT is independently published and executed. Encrypted
published recovery checks format 10 and the unchanged execution journal schema 9.

Deterministic validation does not prove prose-level completeness. FinalReview keeps
the original request and revisions visible beside accepted requirements, identifies
declared operation associations without upgrading them to proof of intent, and
requires explicit review of omissions, extra effects, outputs and cleanup.

The separately authorized [18 October live comparison attempt](../first-attempt-taskplan-live-2026-10-18/README.md)
stopped on uncertain provider completion before its first baseline proposal.
It remains inconclusive and does not change these offline results.
