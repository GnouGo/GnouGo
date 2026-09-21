# Semantic grounding live acceptance

Implementation and acceptance are in progress on `feat/semantic-grounded-planner`, based on `feat/deterministic-planner-v2` at `b0d56c791ad0b330f4eb8646f6941a84dddf79b0`. The base branch is unchanged.

Private requests, model responses and session artifacts remain in encrypted KeyVault records. The table records real model planning attempts; scenarios, when present, use simulated integrations. No generated workflow has been approved or executed.

| Attempt | Source | Session | Coverage | Calls / replans | Outcome |
| --- | --- | --- | --- | --- | --- |
| Designer 1 | `d09e53a` | `a636cc4b80e94167ac224fe323485814` | 129 capabilities, 3/3 pages | 4 / 0 | Stopped before binding: input limit |

Designer prompt: 680 UTF-8 bytes; SHA-256 `8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`. The actual Designer form submitted it unchanged at 2026-09-21 21:35:50 UTC. Provider/model/protocol: OpenAi / `gpt-5.5-2026-04-24` / Chat Completions, verified before dispatch. Settings: medium reasoning, 12,000 input tokens, 8,192 output tokens, eight calls, two replans. All four calls have completed durable receipts; no uncertain dispatch remains. Reported usage: 23,658 input and 15,254 output tokens, estimated EUR 0.501227. Catalog hash: `68e8ae283beaff5e87fad93d7d5d3874a6c1737acbc7bcd76324b3f405be6382`.

The first attempt completed semantic planning and every catalog page. Binding was rejected before reservation because the combined request required an estimated 25,094 tokens. Corrections preserve complete semantic coverage, introduce explicit selection among retained viable matches before loading binding contracts, omit repeated schema annotations from the binding view while retaining every assertion/default, and encode prompt Unicode literally. Business calculations may also ground to specialized declared evaluators, and leaf cleanup actions participate in grounding. Limits and authoritative catalog contracts remain unchanged.

Initial validation: 104 Planning, 853 Flow, 76 Flow.Integrations, 368 Agent.Server and 222 AI.Core tests passed in their latest runs. Warning-free solution build, frontend build, planner package, Native AOT eight-case corpus and trimmed Server EF/SQLite encrypted persistence smoke passed. These checks will be repeated for the final acceptance revision where affected.

Historical two-call measurements describe the old architecture. Current offline native cases use two calls, and direct external cases use three; paged grounding, ambiguous-match selection, fixtures and replanning incur separate calls under the same total allowance.
