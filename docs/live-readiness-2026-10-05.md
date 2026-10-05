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

The campaign upper bound is now **€77.562334/€100**, leaving **€22.437666**. The previous **€2.606753 unknown reservations are unchanged**. No uncertain invocation was resumed and no GitHub feedback was published.

## Remaining gates

Mapping run `focused20261005a` contains three persisted, compiled review artifacts and **zero model calls or executions**. Each requires explicit acknowledgment of `extraction` and `completeness`, at revision 1:

| Variant | Artifact hash | Business behavior |
| --- | --- | --- |
| scalar | `6b00c5600388828cdaaeb2c11490820725eec1574b8ca2a8c537aa10ee3110c8` | Observe complete source, extract observed name, reject missing required data. |
| extended | `2dabfa60db3d46ff862e0bb212a10d824efa125a9edff66ab6c222e051f2f382` | Observe complete source, extract observed name and reference under the changed target. |
| each | `2bc25cea743033d0017e2b58dc8c517c87eedc3b152c2583a405eb77883dbdc4` | Read every source item, extract exactly one row per item in order, verify all 80 rows and warm reuse. |

The review request is pending; acknowledgments have not been populated. Preserve the frozen source/harness `f199a5d7` for execution (an isolated checkout is appropriate when later commits only publish evidence). Never resume a started variant.

**Fresh Amazon and full code-review E2Es have not run.** They remain gated on successful focused mapping execution; the passing small Copilot producer probe does not replace a full code review. No six-run acceptance claim is made. PR #117 remains draft; historical 33/33 evidence and all earlier failed cohorts remain unchanged.
