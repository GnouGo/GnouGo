# Semantic grounding live acceptance

Implementation and acceptance are in progress on `feat/semantic-grounded-planner`, based on `feat/deterministic-planner-v2` at `b0d56c791ad0b330f4eb8646f6941a84dddf79b0`. The base branch is unchanged.

Private requests, model responses and session artifacts remain in encrypted KeyVault records. The table records real model planning attempts; scenarios, when present, use simulated integrations. No generated workflow has been approved or executed.

| Attempt | Source | Session | Coverage | Calls / replans | Outcome |
| --- | --- | --- | --- | --- | --- |
| Designer 1 | `d09e53a` | `a636cc4b80e94167ac224fe323485814` | 129 capabilities, 3/3 pages | 4 / 0 | Stopped before binding: input limit |
| Designer 2 | `9552c17` | `fc81bfc29dc6484088e9f151bd81f2b3` | 129 capabilities, 3/3 pages plus selection | 5 / 0 | Stopped before binding: 13,952 estimated input tokens |
| Chat 1 | `9552c17` | `e93b2a8a08d492a57718b775fb26e4705d81450067882db5514f235fafbf4859` | 129 capabilities, complete catalog page | 3 / 1 | Clarification requested execution approval; left unanswered |
| Designer 3 | `a60d66e` | `832b1dc1a9f34bb4aeb0b762f7f4d83b` | 129 capabilities, 3/3 pages plus selection | 5 / 0 | Stopped before binding: 12,690 estimated input tokens |
| Chat 2 | `a60d66e` | `dbd0d8b1e9b03b32a86e8c9186fecf2d34fb1070a56c9c7d16166b12fd3a0f74` | 129 capabilities, complete catalog page plus selection | 8 / 4 | Stopped: binding contract failures, call allowance exhausted |

Designer prompt: 680 UTF-8 bytes; SHA-256 `8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`. The actual Designer form submitted it unchanged at 2026-09-21 21:35:50 UTC. Provider/model/protocol: OpenAi / `gpt-5.5-2026-04-24` / Chat Completions, verified before dispatch. Settings: medium reasoning, 12,000 input tokens, 8,192 output tokens, eight calls, two replans. All four calls have completed durable receipts; no uncertain dispatch remains. Reported usage: 23,658 input and 15,254 output tokens, estimated EUR 0.501227. Catalog hash: `68e8ae283beaff5e87fad93d7d5d3874a6c1737acbc7bcd76324b3f405be6382`.

The first attempt completed semantic planning and every catalog page. Binding was rejected before reservation because the combined request required an estimated 25,094 tokens. Corrections preserve complete semantic coverage, introduce explicit selection among retained viable matches before loading binding contracts, omit repeated schema annotations from the binding view while retaining every assertion/default, and encode prompt Unicode literally. Business calculations may also ground to specialized declared evaluators, and leaf cleanup actions participate in grounding. Limits and authoritative catalog contracts remain unchanged.

Initial validation: 104 Planning, 853 Flow, 76 Flow.Integrations, 368 Agent.Server and 222 AI.Core tests passed in their latest runs. Warning-free solution build, frontend build, planner package, Native AOT eight-case corpus and trimmed Server EF/SQLite encrypted persistence smoke passed. These checks will be repeated for the final acceptance revision where affected.

Historical two-call measurements describe the old architecture. Current offline native cases use two calls, and direct external cases use three; paged grounding, ambiguous-match selection, fixtures and replanning incur separate calls under the same total allowance.

Designer 2 was submitted at 21:49:39 UTC with the same prompt hash and unchanged settings. All five receipts completed. Reported usage: 28,772 input / 16,972 output tokens; stored estimated cost EUR 0.568338. Semantic selection completed, but the combined binding schema/context still exceeded the input ceiling. Further corrections factor the response schema's common operation fields without removing assertions, omit semantically empty context defaults, and remove the obsolete grounding-stage questions interface.

Chat 1 was submitted through the actual chat composer at 21:49:49 UTC. Its unchanged 2,761-byte prompt hash is `6755b57df5804c26b9dca0c1babe62a31b9d2404ae7b5b96ff174cced1611649`. The generic acceptance runner preserves the existing medium reasoning, 64,000 / 32,768 request ceilings, eight total calls and six configured replans; only the renamed replanning setting changed. Reported usage: 27,529 input / 25,779 output tokens. Stored cost was unavailable (zero without an estimate), not evidence of free usage. Complete grounding identified an unsupported pre-execution approval action and unavailable exact check commands. Atomic replanning asked for execution permission. No answer or approval was supplied. Semantic generation now explicitly leaves FinalReview and execution approval to the host; missing command capabilities must remain fail-closed or be satisfied by a semantically valid alternative.

Designer 3 (22:10:56 UTC) and Chat 2 (22:11:42 UTC) used the same unchanged prompts and settings. Their catalog hash was `3635f655617248b7ec5ab9d4ec793264d70325dc44ade8a58d82eec44b109634`. All reservations have completion receipts. Chat 2 reported 112,526 input / 73,072 output tokens; its stored zero cost lacks an estimate. No scenario or approval was reached. Its trace is `2b29b688ed46a1d9bd78c53d2ff1565c`.

These attempts motivated complete-subgraph binding batches, removal of calculation type assertions, explicit opaque computation boundaries, and precise argument diagnostics. A host defect lost numeric literal values before checking bounded arguments; regression tests now preserve that proof through compilation while allowing alternate values for defaulted workflow inputs. Opacity checks now run before graph construction, including aliases and dynamic projections. No model, token, time or call limit was increased.
