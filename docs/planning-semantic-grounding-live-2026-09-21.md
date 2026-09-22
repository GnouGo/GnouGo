# Semantic grounding: final verification and bounded live campaign

The acceptance criterion is **not met**: the campaign requires a complex Designer and Server-chat success at `FinalReview`, passing scenarios and no blocking diagnostics, with at least three runs on the same final implementation. Five runs used `c30b821`; two reproduced engine defects required the final freeze at `d9f459f42cde5ed92882b16c473a2d932b357bb3`. The sixth run reached `FinalReview` on that revision with 41/41 scenarios passing, complete grounding and no blocking diagnostics. No Designer run succeeded, and only one campaign run used the final implementation. Testing stopped at the six-run cap.

No generated workflow was approved or externally executed. Live planning uses the real model and catalog; scenario executions use isolated simulated integrations.

## Implementation and verification

The [architecture](workflow-planning-v2.md) remains SemanticPlan → complete capability grounding → GroundedPlan → deterministic validation → mechanical PlanningGraph → compile → scenarios → approval. The finish makes four bounded corrections:

- Graph validation shares the conservative contract inclusion checker, including union contracts, numeric constraints, required fields and opacity. Constructed object values retain their known closed shape.
- Static lowering/compiler disagreements stop as host defects before fixtures or model replanning.
- Invocation fallback contracts are checked before issuing a validated grounded-plan receipt.
- Replanning follows a failed callee when the same scenario proves that a workflow-call failure propagated from it. Full failed scenario evidence remains intact and blocking; unrelated wrapper failures remain host defects.

The full suites pass: **170 Planning, 857 Flow, 76 Flow.Integrations, 374 Agent.Server and 222 AI.Core tests — 1,699 total**. Solution/frontend builds, planner packaging, Native AOT eight-case corpus and trimmed Server encrypted EF Core/SQLite persistence smoke pass without build warnings. Existing restart, cumulative accounting, tenant isolation, stale approval, cancellation, cleanup and execution-safety assertions remain covered. [Verification metadata](evidence/semantic-planner-final-verification.json) identifies the tested implementation and log hashes.

The Native AOT corpus uses three calls for its six smaller cases and four for its two complex review cases, with zero replans. Historical two-call measurements describe the old architecture; live phase costs are recorded separately.

## Live campaign

Each request was entered through its actual Blazor UI. Settings were captured before dispatch. Both prompts were unchanged:

- Designer: 680 UTF-8 bytes, SHA-256 `8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`.
- Server chat: 2,761 UTF-8 bytes, SHA-256 `6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649`.

Provider/model/protocol: OpenAi / `gpt-5.5-2026-04-24` / Chat Completions, medium reasoning. Designer retains 12,000 input / 8,192 output tokens, eight calls and two replans. Chat retains 64,000 / 32,768 tokens, eight calls and six configured replans. Both retain 15,000,000 total tokens and 18,000,000 ms; Designer retains its EUR 50 ceiling. No limit, prompt, batching rule or safety boundary was relaxed.

All runs discover the existing 129-capability catalog from nine configured MCP servers. Catalog hash: `3635f655617248b7ec5ab9d4ec793264d70325dc44ade8a58d82eec44b109634`.

| Run | UI | Source | Calls / replans | Coverage | Scenarios | Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Designer | `c30b821` | 2 reserved / 0 | 0/2 pages | Not reached | Stopped: unverified provider transport completion |
| 2 | Chat | `c30b821` | 7 / 4 | 2/2 pages | Not reached | Stopped: repeated invalid capability selection |
| 3 | Designer | `c30b821` | 7 / 2 | 2/2 pages | Not reached | Stopped: invalid string defaults and unvalidated field projections |
| 4 | Chat | `c30b821` | 5 / 1 | 1/1 page | Not reached | Stopped: fallback validation defect; corrected in final freeze |
| 5 | Designer | `c30b821` | 7 / 0 | 2/2 pages | 24/31 passed | Stopped: invalid adapter evidence and propagated-failure classification defect; classification corrected |
| 6 | Chat | `d9f459f` | 8 / 3 | 1/1 page | **41/41 passed** | **FinalReview**, no blocking diagnostics |

[Sanitized attempt evidence](evidence/semantic-planner-live-attempts.json) contains exact revisions, submission times, session/trace IDs, settings, coverage, diagnostics, receipts, usage and phase costs. Private requests, responses, fixtures and planning artifacts remain in encrypted KeyVault records.

## Limitations and accounting

Run 1 has one completed receipt and one uncertain reservation. Its reported usage covers only the completed call; the missing completion is neither replayed nor refunded. Historical uncertain reservations are also preserved. A stored zero monetary cost without an estimate means unknown cost.

Model outputs can still omit required implementations, violate output contracts, access unvalidated fields or supply inadequate scenario evidence within the configured limits. These failures remain fail-closed. The campaign adds no output-specific heuristic and does not claim reliable complex generation from the passing offline corpus.

The detailed history retains 24 earlier attempts, including the recovered Designer/Chat 12 receipts. Historical Chat 10 (`64d3835`, 53/53 scenarios) and Chat 11 (`0e88388`, 60/60) reached FinalReview. Those runs and read-only replays do not satisfy acceptance of the final frozen revision.

PR #100 targets `feat/deterministic-planner-v2`. The original branch remains at `b0d56c791ad0b330f4eb8646f6941a84dddf79b0`.
