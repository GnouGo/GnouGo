# Configuration parity and focused live probes

The latest Amazon run stopped before mapping inference because the benchmark did not hydrate persisted model overrides. Agent.Server already loaded those overrides. A zero-inference inspection now resolves the exact `gpt-5.5-2026-04-24` declaration: 1,050,000 input/context tokens and 128,000 output tokens. The campaign still admits at most 96,000 input and 32,768 output tokens; mapping retains its 8,192 output allowance. No configuration or ceiling was changed.

The benchmark reuses the existing Agent configuration repository and runtime option merge, preserving host/file metadata and persisted override precedence. Provider/model pins cannot change. Readiness prints only declared limits, metadata/configuration fingerprints and availability. Unknown limits fail before paid planning/execution. It neither guesses a nearby model nor treats a campaign ceiling as provider metadata.

The retained code-review responses contain malformed ports inside immutable required conditions. Diagnostics now identify the producer and exact declared ports. When the resolved producer is also outside repair authority, the planner stops for explicit revision even if other bindings are editable. It does not strip Unicode, guess a port, widen repair authority or alter historical requests. Producer references in required conditions are included in existing read-only repair context.

Production changes are confined to those diagnostics, read-only context and repair admission. TaskPlan/compiler structure, `mapping.dynamic`, execution limits and permissions are unchanged.

## Focused probes

See the [benchmark commands](../tests/GnOuGo.Agent.Planning.Benchmark/README.md#bounded-mapping-cohort). The mapping harness compiles three reviewable fixtures without planning inference, persists them before waiting and consumes explicit revision/hash-bound requirement acknowledgments. Scalar, changed-target and independent-collection variants share tenant-local cache evidence; each variant refuses replay after execution starts. Every original item is checked independently, including the large HTML collection. Injected repair is labelled separately from ordinary provider responses.

The independent Copilot probe connects only the actual Copilot MCP through the campaign inference proxy. One disposable Python command writes an observed report and an invocation marker. The probe checks terminal evidence and exact encrypted receipt persistence before cleanup. Verified errors are failed probes, while unknown completion retains the workspace. This is producer validation, not the Amazon/code-review acceptance cohort.

## Validation and rollout

The focused pre-change baseline passed 32 tests. New regressions cover metadata precedence/pins, capability forwarding, unknown limits with zero dispatch, reviewable deterministic fixtures, invalid immutable ports and legitimate Unicode ports. The initial full-suite attempt encountered the previously recorded local port-4317 conflict; the final run uses the existing test-only ephemeral gRPC endpoint. Initial fixture setup failures and publish analyzer feedback are retained in validation evidence; fixes do not weaken production checks.

Candidate `f199a5d705e2a16d8a37607597c54191c261c6e7` passes the full solution with `-warnaserror`: **4,511 passed, zero failed, 13 skipped across 33 projects**. The additional browser skip passes separately with the packaged Playwright module, leaving the twelve historical skips. Release Planning packaging, planning/Copilot Native AOT, current trimmed Server encrypted recovery, current native Copilot encrypted recovery and skill validation pass. The frontend production build also passes. [Validation and retained log hashes](evidence/live-readiness-2026-10-05/validation.json).

Amazon readiness verifies a real local Browser observation, independent XLSX reading and Browser cleanup with zero inference. Code readiness discovers the current producer and runner contracts. These checks are prerequisites, not real-site or repository acceptance.

## Fresh producer execution

`focused20261005a-copilot` passes the real-provider producer oracle on candidate `f199a5d7`: one SDK session, one `python3 probe.py` command with exit zero, exact observed report, verified durable terminal receipt, receipt reload without replay, and disposable-directory cleanup. The command and report read each received an individual one-time approval; no persistent grant or sandbox expansion was used. No false `RUN_NEEDS_RECONCILIATION` occurred.

Measured inference: **3 attempts, 19,375 input tokens, 161 output tokens, €0.090606**, with complete usage receipts. Execution took **92,400 ms**, including interaction waiting. The operation retained 142,427 conservatively reserved tokens under its existing ceilings. [Sanitized producer evidence](evidence/live-readiness-2026-10-05/copilot-probe.json).

After that producer probe, the campaign upper bound was **€77.562334/€100**, leaving **€22.437666**. The previous **€2.606753 unknown reservations are unchanged**. No uncertain invocation was resumed and no GitHub feedback was published.

## Remaining gates

Mapping run `focused20261005a` contains three compiled review artifacts. The user explicitly approved their `extraction` and `completeness` requirements at revision 1 before execution:

| Variant | Artifact hash | Business behavior |
| --- | --- | --- |
| scalar | `6b00c5600388828cdaaeb2c11490820725eec1574b8ca2a8c537aa10ee3110c8` | Observe complete source, extract observed name, reject missing required data. |
| extended | `2dabfa60db3d46ff862e0bb212a10d824efa125a9edff66ab6c222e051f2f382` | Observe complete source, extract observed name and reference under the changed target. |
| each | `2bc25cea743033d0017e2b58dc8c517c87eedc3b152c2583a405eb77883dbdc4` | Read every source item, extract exactly one row per item in order, verify all 80 rows and warm reuse. |

The frozen source/harness `f199a5d7` executed ten of eleven cases: **nine passed, independent-collection cold extraction failed, and its warm-cache case was not attempted**. All scalar and changed-target oracles passed, including zero-inference JSON cache reuse, HTML decoding, expected missing-data rejection and separately labelled fault-injection repair. Ten paid calls used 46,357 input / 1,639 output tokens and **€0.250763**; one injected attempt incurred no provider call. The campaign upper bound became **€77.813097/€100**, retaining every unknown reservation. [Complete sanitized evidence](evidence/live-readiness-2026-10-05/mapping-focused20261005a.json).

The 80-item probe read all original observations. Generation sampled three complete items from 1,119,671 source bytes; the repair request was 45,596 bytes, below the unchanged allowance. Both returned expressions were valid extractions. Deterministic replay exposes the actual failure: recompiling the same nonbacktracking regex for each item exhausts Jint's cumulative 50,000,000-byte allocation allowance around item 20. A scoped sandbox correction reuses each compiled regex within one evaluation. It changes no expression semantics, inference attempts, `mapping.dynamic` code, cache identity, plan or approval contract, and resets no limits. Two regressions fail before this correction and pass afterward; all 66 focused mapping tests pass. Native AOT additionally checks 80 ordered HTML extractions.

The failed run and its receipts remain retained; no started variant is replayed. Subsequent validation uses the separately frozen candidate and run below.

## Corrected mapping candidate

Candidate **`34bfd6040181cf789a5472506821c5a7f5e2e552`**, matrix **`focused20261005b`**, passes **11/11 independent execution oracles**. This matrix is separate from the failed candidate above. It uses the same prompts, data, targets and oracles; all three business artifact hashes are unchanged from the user's explicit acknowledgment.

The 80-item cold run uses **one model call, zero repairs**, two complete examples and a 31,406-byte request. All 80 original items are processed and independently checked. The warm run validates all 80 items with **zero model calls**, taking 1,272 ms in the mapping step versus 14,070 ms cold. Complete workflow times were 81,464 ms cold and 72,064 ms warm, including the 80 actual MCP reads, durable journaling and confirmation waiting; these are not pure mapping latency measurements. The scalar HTML response needed one natural provider repair and then passed. Injected repair remains separately labelled. Missing required data correctly failed its contract and passed the expected-failure oracle.

The matrix used ten paid calls, **22,109 input / 1,606 output tokens, €0.141668**, plus one injected non-provider attempt. Generation fixtures required zero planning calls/repairs. [Matrix evidence](evidence/live-readiness-2026-10-05/mapping-focused20261005b.json).

Validation after the correction: **4,513 passed, zero failed, 13 skipped across 33 solution projects**, with `-m:1 -warnaserror`. The additional browser skip passes separately, leaving twelve historical skips. Flow.Core Release packaging, planning Native AOT including the 80-item case, frontend production build and local Browser/Document readiness pass. No storage/serialization contracts changed; the earlier published encrypted-recovery evidence remains separate. [Validation counts and log hashes](evidence/live-readiness-2026-10-05/regex-reuse-validation.json).

## Fresh E2E planning and remaining execution gates

Frozen cohort **`focusedlive20261005a`** uses candidate `34bfd604`, unchanged prompts/oracles/limits and new identities. Disposable toolchains resolve Node 24.20.0, repository-selected pnpm 10.34.5 and Python 3.11.13. The recorded SmartGuide PR head is still available. Readiness independently checks a local Browser page, XLSX cells and Browser cleanup with zero inference.

| Measurement | Amazon | Code review |
| --- | ---: | ---: |
| Planning calls / physical attempts | 5 / 5 | 8 / 8 |
| Repairs / explicit review revisions | 1 / 1 | 2 / 3 |
| Discovery reads | 2 | 6 |
| Verified input / output tokens | 54,810 / 15,554 | 203,065 / 29,206 |
| Planning latency | 206,104 ms | 345,276 ms |
| Planning cost | €0.661076 | €1.688241 |
| Current state | Final review, revision 6 | Stopped, revision 14; call allowance exhausted |
| Workflow execution | Not started; acknowledgment pending | Not started; invalid proposal |

Amazon's first proposal was withheld for guessed selectors, full-observation interpretation and placeholder-cell behavior. Its revision explicitly consumes snapshot pages, uses independent source-grounded compaction, conditionally accepts observed consent, visits products sequentially, guards workbook publication and closes the browser. Explicit interpretation steps remain visible in the [artifact review](evidence/live-readiness-2026-10-05/amazon-review.md). It is not approved automatically: the four requirement acknowledgments are pending, bound to revision 6 and artifact hash `0588d6083951556a1c7fe4cf397b46221944374148c89a0b1d7e07cc6edbb05e`.

Code proposals were retained with their failures: double selection of scalar ports; missing conditional exports and sibling-scope references; and report writing only on the normal path before cleanup. Explicit revisions preserved accepted requirements and cumulative accounting. The last proposal attempts failure-path preservation but still has missing exports and invalid cross-scope dependencies. The existing compiler rejects it, and the eight-call ceiling stops further dispatch. No Copilot session, command execution, report or cleanup ran in this E2E. The earlier successful small producer probe does not establish full code-review success. No planner or integration checks were weakened to admit these proposals.

Zero-inference replay confirms the Amazon artifact still compiles on the isolated frozen checkout and the code proposal deterministically fails the same scope checks; saved sessions remain unchanged. [Amazon evidence](evidence/live-readiness-2026-10-05/focusedlive-amazon.json), [code evidence](evidence/live-readiness-2026-10-05/focusedlive-code.json), [six-slot cohort report](evidence/live-readiness-2026-10-05/focusedlive20261005a.json).

Campaign upper bound: **€80.304082/€100**, leaving **€19.695918**, including the unchanged **€2.606753** historical unknown reservations. All new calls have completion/usage receipts. No uncertain invocation was resumed, and no GitHub feedback was published.

The E2E cohort remains **0/6, incomplete**; repetitions two and three are unattempted. PR #117 stays draft. Historical 33/33 evidence and every earlier failed cohort remain unchanged. To continue Amazon after explicit acknowledgment, use source `34bfd604` and the retained revision/hash-bound approval through the existing `--review-command`; the isolated checkout is `/private/tmp/gnougo-live-34bfd604`. Never resume any started or uncertain execution.
