# Global MCP shortlist and compact responses

This deterministic pass continues from `f46a40d` on `feat/flow-hybrid-planning-v9` (issue #112, draft PR #113). It preserves TaskPlan, compilation, execution, approval, medium reasoning and existing allowances. No paid inference or external workflow is run. The [previous discovery measurements](compact-mcp-discovery.md) and historical recordings remain unchanged.

## Behavior

Selected sources still rank their complete metadata before returning eight-entry pages. One global ranking now selects at most eight optional detailed contracts from the current pages, without source quotas, using shared inverse document frequency and the existing name/field/description weights. TaskPlan-required contracts are additional and never pruned. Global means the currently exposed candidates of progressively selected sources, not every undiscovered tool.

The latest completed batch's normalized effective queries are retained as `PresentationQuery`; older sessions fall back to request-derived ranking. Refinement and snapshot-bound continuation remain available, including refinement of small sources whose tools fell outside the shortlist. Ranking establishes no bindings, types or permissions. Only admitted candidates and TaskPlan-selected operations are resolved. Cached contracts stay in state without automatically entering every prompt or the executable catalog.

Fixed-operation repairs omit discovery indexes, alternatives and source descriptions that their scope cannot use. Exact selected contracts, the lossless baseline, repair diagnostics and coverage limitations remain. Mandatory context still passes through the unchanged admission check.

New strict response schemas omit `nullable: false`, `required: true` and absent `default: null`. Existing DTO initializers reconstruct only those representation constants. Nullable types, optional fields, actual literal defaults, array item types, objectives, dependencies and exports remain explicit. Optional workflow/group inputs still require defaults; optional object fields do not. Saved requests retain their original schemas and identities. No stored TaskPlan or approval is rewritten.

## Frozen coding/review measurements

`FrozenReviewDiscoveryTests` uses the unchanged sanitized recording for session `ec7ee0aa2d1444d8a3b1fbbc7b8f1558`. The comparison renders new requests from frozen state; it makes zero metadata reads or inference calls. Estimates include the complete response schema. The baseline is the recorded `f46a40d` measurement, not a retained alternative planner.

| Attempt | Before prompt bytes | After prompt bytes | Before estimated input | After estimated input |
| --- | ---: | ---: | ---: | ---: |
| 1 | 7,610 | 7,716 | 8,438 | 9,210 |
| 2 | 51,243 | 35,037 | 22,819 | 18,344 |
| 3, unconfirmed | 54,598 | 42,232 | 23,937 | 20,743 |
| 4 | 54,598 | 42,232 | 23,937 | 20,743 |

The maximum input estimate falls **13.3%**, maximum prompt bytes **22.6%**, and cumulative estimates **12.8%** (79,131 → 69,040). Every recorded generation state stays below 21,000. Strict schema alternatives increase the initial request; that cost is included. The earlier pre-compaction maximum was 58,800, reported separately in the historical document.

The same TaskPlan shrinks **2.6%**, from **16,784 to 16,344** minified UTF-8 bytes. Complete response JSON falls **16,818 → 16,378** bytes, an estimated **5,460 serialized tokens**, excluding reasoning. All **26 tasks and six transforms** remain, with identical compiled YAML and approval material. The much larger persisted response envelope contains repeated response copies and is not a generated-output measurement.

The historical run retains four charged attempts, three confirmed responses and its unconfirmed reservation. Its final output was 9,913 tokens, including 5,614 reasoning tokens. No new live output-token, reasoning, latency, call-count or reliability gain is claimed. The separate two-source scripted regression still needs **two planning calls, two metadata reads and eight contract resolutions**; complete estimates are **8,970 / 10,463**.

## Validation and limits

Tests cover three 1,000-tool sources, far-tail retrieval by names/descriptions/field names, more than four relevant tools from one source, stable ordering, policy filtering, refinement/recovery and exact mandatory contracts. Existing continuation, cache, unavailable-source, safety, approval and eight business execution scenarios remain. Historical responses replay against saved schemas; only separately labelled synthetic corrections are projected to new wire schemas. Independent execution oracles and original fixture files are unchanged.

Lexical relevance is not semantic completeness: needed tools can remain outside the initial shortlist. Compact indexes, refinement, continuation and automatic resolution of TaskPlan selections preserve widening. No live test establishes natural model selection quality.

A synthetic binding-only repair of the complete retained plan still estimates **24,487 input tokens**, above a 24,000 allowance, after removing unusable discovery context. All ten required operation contracts and the baseline must remain; admission therefore stops safely. Generation headroom is not a guarantee that every repair fits. No limit is increased to hide this case.

Exact validation revisions and CI results are recorded in PR #113; raw failures and subsequent results are retained under `artifacts/global-discovery-2026-09-27/`. The existing Windows Cmd CI blocker and real Copilot sandbox/command-execution limitation remain separate. The PR stays draft and is not merged.
