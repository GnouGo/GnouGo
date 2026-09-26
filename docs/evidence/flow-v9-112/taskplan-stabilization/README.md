# TaskPlan stabilization: acceptance passed, one repair failure retained

The frozen candidate `d636c998acd08b17cd21cb4a9b85c0911b3290ba` completed exactly nine live outcomes: **8/9 correct**, with **no unsafe or incorrectly approved executions observed**. It meets the agreed minimums of 3/3 conditional, 3/3 French review and 2/3 distractor review. Implementation and paid evaluation stopped after this cohort. PR #113 remains draft because real sandboxed Copilot command edit/test execution remains unverified; this mocked execution benchmark does not resolve that limitation.

The candidate continues from `80e50fa`. Changes are confined to the existing response schema, compiler/source maps and scoped repair. The pipeline remains Requirements → progressive discovery → LLM TaskPlan → deterministic compilation → PlanningGraph → YAML → validation → scoped TaskPlan repair → approval. Public contracts are unchanged, planning storage remains format 10 and execution journals remain schema 9. No planning abstraction, model phase or infrastructure refactor was added.

The reviewable implementation commits are `d472250` (contracts and composite compilation), `0c584d1` (scoped repair and diagnostics) and `d636c99` (pre-evaluation freeze/evidence). All subsequent changes in this pass are reporting only.

The fixes have the following effects:

- The response schema expresses the existing literal-default requirement for optional workflow/group inputs without requiring defaults for optional object fields. Compilation collects independent workflow/group input errors together and does not invent defaults or change requiredness.
- Discovery sources and continuation cursors come from unconsumed discovery receipts. Exhausted discovery requires a plan and null discovery fields. Conflicting actions still fail deterministically; persisted pending requests retain their original response schema.
- Composite scope outputs compile through existing typed `set` stages with stable IDs and authoritative schemas. They run after declared cleanup, with existing presence/availability guards. Opaque-output checks and runtime expression inference are unchanged.
- Validation findings map to semantic tasks and business ports, including group inputs, nested outputs and the confirmation wrapper. Unauthorized edits identify the changed slots, while rejected repairs preserve the original baseline, cached contracts, choices, interfaces, ordering and cumulative budgets. A remaining branch-export repair limitation is described below.
- The malformed cleanup producer ID remains invalid. A minimal correction passes; the retained broad rewrites still fail. Approval continues to require deterministic reproduction of the reviewed artifact, including literal agent scope.

Four sanitized synthetic fixtures preserve the semantic response sequences and rejected repairs from the previous cohort. They omit provider prompts, raw receipts, credentials and host settings. Original encrypted evidence is untouched. All 36 prior evidence files match [their pre-change hashes](prior-evidence-hashes.json).

The initial regressions produced six failures and one pass. The pass established that the minimal cleanup-ID correction was already supported. Compiler/contracts fixes reduced failures to three, then scoped repair fixes passed all seven initial regressions. Additional coverage brings the planner suite to **153 passing tests**. [Regression progress](regression-progress.json) records the intermediate results; no independent oracle was weakened.

The live comparison uses the same three cases and three repetitions in every cohort:

| Case | Parent `46c2c77` | Prior candidate `87acc5f` | Stabilization `4cb8586` | Previous TaskPlan `78b8b34` | New `d636c99` |
| --- | ---: | ---: | ---: | ---: | ---: |
| conditional | 3/3 | 3/3 | 2/3 | 3/3 | **3/3** |
| review_french | 0/3 | 2/3 | 3/3 | 1/3 | **3/3** |
| review_distractors | 0/3 | 2/3 | 1/3 | 1/3 | **2/3** |

There is no per-case correctness regression against any retained comparison cohort. The [audited comparison](comparison.json) and [acceptance record](acceptance.json) preserve both the comparison against the best retained result and the previous TaskPlan cohort. Three repetitions support this bounded comparison, not a broad reliability claim.

| Same nine-outcome case mix | Physical attempts | Median calls | Median latency (s) | Known input tokens | Known output tokens | Exact usage complete |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Parent | 47 | 5 | 122.521 | 138,510 | 95,640 | Yes |
| Prior candidate | 41 | 4 | 150.046 | 278,068 | 72,206 | Yes |
| Stabilization | 40 | 4 | 351.654 | 229,606 | 67,288 | No |
| Previous TaskPlan | 31 | 3 | 100.116 | 129,035 | 39,462 | No |
| New candidate | **23** | **1** | **21.020** | **103,377** | **26,453** | **No; bounded** |

Historical full-eight-case medians of 4 and 2.5 use a different case mix. The matched comparison above reports calls, tokens and latency separately. Unknown transport usage is excluded from known token counts and retains its full reservation.

Every new outcome is retained in the unmodified [runner output](live.jsonl), including the unsuccessful second distractor repetition:

| Case | Repetition | Correct | Physical attempts | Repairs | Latency (s) |
| --- | ---: | --- | ---: | ---: | ---: |
| conditional | 1 | Yes | 1 | 0 | 13.239 |
| review_french | 1 | Yes | 1 | 0 | 20.679 |
| review_distractors | 1 | Yes | 5 | 0 | 347.087 |
| conditional | 2 | Yes | 3 | 0 | 616.903 |
| review_french | 2 | Yes | 1 | 0 | 20.048 |
| review_distractors | 2 | **No** | 6 | 2 | 124.735 |
| conditional | 3 | Yes | 1 | 0 | 10.644 |
| review_french | 3 | Yes | 1 | 0 | 21.020 |
| review_distractors | 3 | Yes | 4 | 0 | 40.904 |

The remaining failure is a semantic branch-export and repair-scope limitation. The plan put lint, unit tests and integration tests in explicit parallel branches, declared no branch business outputs, then referenced those internal producers directly from six root outputs. The compiler reported only the first unavailable reference, `/root/outputs/lint_evidence`, and authorized repair of only that binding. The first repair replaced unrelated inputs, tasks, outputs and cleanup and was correctly rejected. The second repair added the missing branch exports and rebound all six outputs through the parallel container; those related changes exceeded the narrow permitted scope and were also rejected. The baseline remained intact and the workflow never reached approval or execution. [Precise diagnostics and changed paths](remaining-failure.json) are retained. No implementation change was made after discovering this limitation.

Successful review plans passed the independent nominal, failed-check, incomplete-review, rejected-confirmation, changed-revision, cancellation, workflow-denied and permission-unavailable variants. Conditional plans passed both branches. This is real model planning with simulated capability execution, not evidence of real repository edits or external publication.

The [freeze contract](freeze-contract.json) was committed before paid dispatch; [the dispatch manifest](freeze.json) records the final source revision and hashes. Model and limits were unchanged: `gpt-5.5-2026-04-24`, medium reasoning, 96,000 input tokens, 32,768 output tokens, eight physical attempts per session, two repairs and the pinned transport policy. Implementation, harness, operation metadata and independent oracle hashes still match. No pilot, replacement repetition, baseline rerun, allowance reset or request-identity restart occurred.

| Campaign `flow-v9-112` accounting | Known EUR | Reserved EUR | Upper bound EUR |
| --- | ---: | ---: | ---: |
| Before cohort | 29.4212 | 12.8511 | 42.2723 |
| This cohort | 1.1529 | 2.5742 | 3.7271 |
| After cohort | **30.5741** | **15.4253** | **45.9994 / 50** |

The cumulative amount is an upper bound including reservations, not an invoice total. Two new uncertain physical attempts, in distractor repetition 1 and conditional repetition 2, remain charged. Campaign physical attempts increased from 483 to 506; uncertain attempts increased from 10 to 12. Approximately EUR 4.0006 remains, but the stopping rule prohibits spending it in this pass. [Before](campaign-before.json) and [after](campaign-after.json) retain the original accounting. [Run inspection](run-inspection.json) preserves terminal states, request identities and repair history; original responses and receipts remain encrypted.

The original runner summary has `gates_passed: false` and returned exit code 1 because it applies historical aggregate gates. Those are not the frozen stabilization acceptance rule. The passing best-per-case comparison applies the predeclared thresholds without changing outcomes. Its `original_comparison` retains an inconclusive historical accounting view; the existing read-only closed-HTTP audit supplies bounded usage proofs without changing historical records or refunds. An initial read-only query mistakenly selected parent phase `measured` and found no parent rows. [That inconclusive query](comparison-missing-parent-phase.json) is retained; the corrected query uses the recorded `fixture` cohort. Neither comparison dispatched a model request.

Validation completed before paid evaluation:

| Check | Result |
| --- | --- |
| Full .NET solution | 2,860 passed across 33 projects, zero failures; five existing opt-in live tests skipped |
| Planner tests | 153 passed, including the new regressions and all eight business scenarios |
| Python | 287 core + 27 CLI + four script tests passed; lint and wheel/sdist checks passed |
| Frontends | All nine builds passed |
| Independent Flow packages | Core, Planning, Integrations, Copilot and Persistence passed package/dependency checks |
| Serialization / Native AOT | All eight planning smoke scenarios; Flow CLI, Flow.Server and Copilot MCP publications passed |
| Trimmed publication | Agent.Server passed |
| Published encrypted persistence | Execution receipts/restart, human answers, revisions, tenant isolation and no plaintext workflow payloads passed; format-10 planning and incompatible-record isolation passed |
| Frozen-source CI | 32 successful checks; four release-only skips, no skipped required dependent job |

[Deterministic results](deterministic-validation.json), [publish commands and initial results](publish-results.json), [final publish diagnostic audit](publish-warning-audit.json), [CI checks](ci-checks.json), [CI job details](ci-runs.json) and [CI log hashes](ci-log-proofs.json) retain the validation evidence. [Local log/TRX hashes](local-validation-log-hashes.json) identify the retained files under `artifacts/taskplan-stabilization-validation/`.

The full solution initially hit `MSB3552` because an ignored stale directory literally named `bin\Debug` contained build-host artifacts. It was quarantined rather than deleted, and the clean run passed without a source change. An initial CLI publication with `-warnaserror` promoted the repository's documented EF Core 10.0.12 experimental notices; publication using the existing audited settings passed. Only those exact existing notices were accepted, with published encrypted-persistence coverage. All other compiler, frontend, trim and AOT diagnostics were warning-free. A scratch collector also misread the localized zero-warning summary as a warning; an exact diagnostic-line audit corrected that reporting error without another build. Initial attempts remain retained.

All required workflows completed successfully at the evaluated source `d636c99`: [main build](https://github.com/GnouGo/GnouGo/actions/runs/36105696411), [deterministic planner](https://github.com/GnouGo/GnouGo/actions/runs/36105696017), [frontend/Python dependencies](https://github.com/GnouGo/GnouGo/actions/runs/36105696010), [six-platform ProxyCopilot AOT](https://github.com/GnouGo/GnouGo/actions/runs/36105696100) and [standalone ProxyCopilot](https://github.com/GnouGo/GnouGo/actions/runs/36105696005). The four skips were release publication jobs (release, release-only AOT, PyPI and NuGet), not required dependent validation. Reporting commits can trigger another CI run; the evaluated source and its completed CI stay pinned here.

All nine outcomes, failures, inconclusive accounting/query views and reservations are preserved. Delivery after the cohort is limited to this evidence commit and the PR description update. No merge, further implementation or paid evaluation is part of this pass.
