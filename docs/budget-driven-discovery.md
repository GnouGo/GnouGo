# Budget-driven discovery

Implementation `a8032c0` follows `52683a4`. TaskPlan, compilation, approvals, planning format 10,
execution journal schema 9 and saved limits are unchanged. No live inference or
external workflow was run. See [PR #113](https://github.com/GnouGo/GnouGo/pull/113)
for the exact final revision, completed CI and known blockers.

## Behavior

Optional contracts are packed in deterministic global relevance order up to 90%
of the saved **complete-request** input allowance: 21,600 estimated tokens at
24,000, including the response schema. There is no fixed contract count. Oversized
or unavailable candidates leave room for later candidates; omitted operations
remain discoverable and selectable. Mandatory contracts are never trimmed and may
use the full hard allowance. Excess mandatory context still stops before dispatch.

The request and accepted requirements always contribute to global ranking. Each
source adds only its latest effective receipt query, using the existing shared IDF
and metadata weights. Duplicate query terms count once. Receipt cursors, complete
source paging, exact contract checks and cached metadata remain authoritative;
ranking establishes neither compatibility nor permissions.

Before the first plan, reserve one proposal and one repair when allowed. The
default eight-call session can therefore spend six calls on discovery, then one
proposal and one repair. Earlier proposals may use both repairs. Explicit zero
repairs and smaller call budgets remain authoritative. Fixed-operation and final
repair restrictions, safe null-plan termination and exhausted-session protections
remain. Pending requests replay their original prompts, schemas and identities.

## Retained failure and measurements

Session `727492f0ce544e598b45325dbcd27a94` used five discovery responses and then
returned a null plan on call six: 17 pages, 90 distinct capabilities, no repairs,
no pending request. Its 13 revisions, six requests/schemas/responses and accounting
are preserved in the sanitized `retained-discovery-incomplete.json` fixture.
Encrypted originals were checked unchanged. Recovery must still reproduce its
historical `DISCOVERY_INCOMPLETE`; the new policy cannot turn that response into a
successful plan.

Fresh presentation of the same sanitized metadata, compared with `52683a4`:

| Request | Complete estimate before → after | Prompt UTF-8 bytes before → after | Optional contracts before → after |
| --- | ---: | ---: | ---: |
| 1 | 9,312 → 9,336 | 8,022 → 8,093 | 0 → 0 |
| 2 | 19,485 → 21,479 | 38,321 → 44,302 | 8 → 11 |
| 3 | 22,102 → 21,484 | 45,863 → 44,008 | 5 → 4 |
| 4 | 23,493 → 21,517 | 50,032 → 44,105 | 7 → 5 |
| 5 | 23,853 → 21,533 | 51,422 → 44,463 | 8 → 6 |
| 6 | 20,354 → 21,598 | 44,480 → 44,041 | 8 → 12 |

Maximum estimate: **23,853 → 21,598**. Cumulative: **118,599 → 116,947**.
Sanitization makes these slightly smaller than the original live maximum of
23,854. These are context-size probes, with zero inference or metadata reads;
contract resolution must still succeed before newly issued detailed context is
used. Fewer contracts can fit when ranking prioritizes larger contracts.

The older frozen review retains all four attempt identities, including its
unconfirmed reservation:

| Attempt | Complete estimate before → after | Prompt bytes before → after | Optional contracts after |
| --- | ---: | ---: | ---: |
| 1 | 9,312 → 9,336 | 8,022 → 8,093 | 0 |
| 2 | 18,040 → 21,560 | 34,123 → 44,685 | 14 |
| 3, unconfirmed | 20,574 → 21,596 | 41,725 → 44,793 | 12 |
| 4 | 20,574 → 21,596 | 41,725 → 44,793 | 12 |

This intentionally uses more available context: cumulative **68,500 → 74,088**.
TaskPlan/response JSON remains **16,344/16,378 bytes**, with the same 26 tasks and
six transforms. The synthetic full-plan repair estimates **24,622**, above the
24,000 hard allowance; it still stops safely. No token-limit increase, live output,
reasoning reduction or reliability improvement is claimed.

## Deterministic acceptance and limits

A separately labelled synthetic schedule uses the existing review's ten exact MCP
contracts and unchanged complete TaskPlan. Six scripted discovery responses then
one proposal reach final review within seven calls, without approval or execution.
Nine contracts fit in its final optional context; all ten simultaneously would
exceed the soft target. The omitted operation remains selectable and exact
resolution preserves all ten contracts in the compiled artifact. The fixture's
synthetic catalog contains only these fully recorded contracts; it is not a live
selection-quality evaluation against the complete producer catalogs. It uses nine
metadata reads and ten exact-contract resolutions. Complete input estimates are
8,040 / 19,946 / 19,606 / 19,606 / 19,606 / 19,606 / 19,200 (125,610 cumulative);
prompt bytes are 5,922 / 42,037 / 41,019 / 41,019 / 41,019 / 41,019 / 41,020.

The two-source product scenario retains two scripted calls and two metadata reads;
optional resolutions increase from eight to twelve, with no rereads after adapter
recreation. Its two requests estimate 9,095 and 10,750 input tokens. Large multi-source catalogs, earlier-page and far-tail retrieval,
source-scoped refinements, mandatory overflow, unavailable/conflicting contracts,
recovery, scoped repair and all eight business oracles remain covered.

Local validation passes: **3,367 .NET tests** across 33 projects (448 planner,
462 Agent.Server; five existing opt-in skips), **318 Python/script tests**, the
Agent frontend build, five independent Flow packages, benchmark **build only**,
and published macOS ARM64 Native AOT smoke. Builds produce no warnings; skill
frontmatter and current documentation links pass. Final-revision CI is recorded
in the linked draft PR, including required skipped jobs and any blockers.

Initial failing regressions and subsequent validation are retained under
`artifacts/budget-driven-discovery-2026-09-28/`. The existing Windows Cmd timeout
and real Copilot sandbox/command-execution limitation remain separate. A stopped
live session is not silently resumed: deploy the update, create a new session and
explicitly approve any resulting workflow. Keep the PR draft; do not merge.
