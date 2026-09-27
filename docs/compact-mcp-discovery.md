# Compact MCP discovery

This pass continues from `882baf7` on `feat/flow-hybrid-planning-v9` (issue #112, draft PR #113). TaskPlan, compilation, execution, approval and storage formats remain unchanged. No paid inference or external workflow was run.

Complete selected-source metadata is ranked before pagination using generic normalized token overlap and inverse document frequency. New pages contain eight compact index entries; at most four candidates per source receive complete contracts. Refinement and query/snapshot-bound continuation use the existing discovery action. Exact metadata is retained, never replaced by the preview. Only TaskPlan-selected operations enter the executable catalog. Saved pending requests retain their original wire contract, prompt, cursor, identity and accounting.

## Frozen review measurement

The unchanged sanitized recording for session `ec7ee0aa2d1444d8a3b1fbbc7b8f1558` supplies actual producer business-port schemas and original issued requests. `FrozenReviewDiscoveryTests` rebuilds only their context presentation, including the response schema in the existing conservative estimator. It measures shortlisted contract presentation before resolution; it does not invent new model responses, claim that the shortlist selects the final plan, or replace recorded evidence.

| Retained attempt | Before prompt bytes | After prompt bytes | Before complete tokens | After complete tokens | Detailed candidates |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 6,013 | 7,610 | 7,592 | 8,438 | 0 |
| 2 | 87,811 | 51,243 | 34,608 | 22,819 | 12 |
| 3, unconfirmed | 160,578 | 54,598 | 58,800 | 23,937 | 11 |
| 4 | 160,578 | 54,598 | 58,800 | 23,937 | 11 |

The largest complete estimate falls **59.3%**; its prompt bytes fall **66.0%**. The sum over all four retained identities falls from **159,800 to 79,131** estimated tokens (**50.5%**). The initial discovery request grows because query/refinement choices and source coverage are explicit. Every newly rendered request fits 24,000 without changing saved production defaults. The comparison makes zero metadata reads or inference calls; the recording retains six pages and four charged attempts, including the unconfirmed third attempt.

Output serialization is unchanged: the recorded final response envelope is **80,593 minified UTF-8 bytes**, the initial responses 7,557 and 2,578 bytes; the unconfirmed attempt has no response. No claim is made about reducing generated output tokens or model turns on this live scenario. Required selected contracts or a large repair baseline can still exceed a saved allowance and must stop admission.

The separate two-source scripted workflow remains **two planning calls**, with complete estimates **8,169 / 9,368**, two discovery pages and nine cached exact-contract resolutions (eight shortlist candidates plus one selected operation). Only its three selected operations enter the executable catalog. This checks framework overhead, not live model selection.

## Validation and limitations

Focused tests cover 1,000-tool retrieval by name/description/field, far-tail placement, reordered listings, normalization, ties, refinement, complete continuation, changed snapshots, cache reuse, unavailable sources, optional-context exclusion and mandatory-contract retention. Recovery retains issued requests and rejects normalized duplicate queries before page reads. Existing business execution oracles, scoped repair and approval checks remain intact. The scripted stress model now requests further pages only while its unchanged business plan lacks a required operation; it no longer exhausts every distractor page.

Validation results and exact revisions are recorded in PR #113. Raw deterministic logs, including the pre-fix prompt failures, remain under `artifacts/compact-discovery-2026-09-27/`. Historical encrypted evidence is untouched. The existing Windows x64 Cmd CI blocker and real Copilot sandbox/command-execution limitation remain separate. No limits were raised, no workflow was approved or externally executed, and the PR remains draft.
