# Controllable discovery: deterministic evidence

This pass starts at `11fb1cf` and preserves the TaskPlan architecture, planning
format 10, execution journal schema 9, medium reasoning, and saved limits. It runs
no paid inference, external workflow or real Copilot task.

## Retained failure

Session `6a8d53066ead425cb50d6b9a4e12e6b1` inspected 20 pages containing 74 distinct
capabilities. Six discovery responses were followed by `plan: null` on call seven;
there were no repairs. The last request omitted a previously presented review
publication contract and its index entry. Ranking displaced contracts while the
latest-page-only index hid earlier identities. This establishes a presentation
defect, not the model's private reason for declining.

All seven responses, original requests/schemas, 15 revisions and accounting are
preserved in
[`retained-discovery-displacement.json`](../tests/GnOuGo.Agent.Server.Tests/Fixtures/DiscoveryBudget/retained-discovery-displacement.json).
SHA-256: `c426d327c8b73f22d7d0fe30b96e43d839a08ecfeb930b747b3e409d8b79906b`.
Original encrypted records were left unchanged. Historical replay retains the
same terminal finding and identities; it does not invent a successful response.

## Contract inspection in the existing loop

`discoveryRequests` still contains one to four requests, exclusive with a TaskPlan.
For page/query requests, `operationIds` is null. A list instead requests exact
contracts for already-discovered operation IDs from that source, with `cursor`
and `query` null. The list replaces only that source's inspection selection; an
empty list clears it, and ordinary pagination preserves it. The complete batch
is checked before metadata fetching. Unknown, cross-source, duplicate,
policy-excluded and ambiguous IDs are rejected.

Inspection consumes the existing discovery allowance, not a new phase. Exact
fetching uses existing APIs and receipts; it never calls a model. Requested
contracts stay visible alongside TaskPlan-required contracts. Optional contracts
fill the remaining 90% target. An unavailable requested contract stops with
`DISCOVERY_CONTRACT_UNAVAILABLE`; oversized mandatory context stops at the saved
hard limit. No contract is silently weakened or dropped.

The compact directory now uses retained identities and port names. It is bounded
against mandatory context before optional details are packed, with omissions
counted. Full contracts include source/name identity and omit duplicate index
entries. Closed discovery removes unusable source descriptions, queries and
continuations. Ranking still uses full metadata and the request/requirements base
with source-scoped refinements. Inspection grants no execution permission; only
operations used by the validated TaskPlan enter its executable catalog.

Pending historical requests use their original schemas, prompts and identities.
The additive nullable discovery fields do not alter TaskPlan, compilation,
approval material or execution journals.

## Measurements

These are conservative complete-request estimates, including the response
schema, from the same sanitized snapshots. They are not provider usage or a live
reliability comparison. The automatic presentation probe makes no metadata reads
or model calls and preserves the historical null-plan outcome.

| Request | Input estimate before → after | Prompt bytes before → after | Detailed operations before → after |
| --- | ---: | ---: | ---: |
| 1 | 9,336 → 9,721 | 8,093 → 8,787 | 3 → 3 |
| 2 | 21,513 → 21,474 | 44,095 → 41,222 | 13 → 12 |
| 3 | 21,337 → 21,568 | 43,567 → 40,959 | 7 → 7 |
| 4 | 21,457 → 21,583 | 44,235 → 41,358 | 10 → 9 |
| 5 | 21,467 → 21,538 | 43,648 → 40,514 | 10 → 8 |
| 6 | 21,577 → 21,558 | 43,049 → 39,507 | 10 → 7 |
| 7 | 21,442 → 21,556 | 47,744 → 48,086 | 12 → 13 |

Cumulative input estimate: **138,129 → 138,998**. Some contexts deliberately use
more space; this is a controllability correction, not a claimed token reduction.
The response schema now advertises explicit inspection. Detailed counts include
three registered operations. Automatic ranking alone is not asserted to choose
every required contract.

A separately identified scripted regression retains all **74** discovered
capabilities and their distractor metadata. It reuses the first five discovery
responses, explicitly requests ten review contracts on call six, and submits the
unchanged previously retained review TaskPlan on call seven. Base-query tokens
affected by sanitizing private paths are mapped to their original frozen pages;
refinement queries and page contents are unchanged.

- Final review: **7 calls, 0 repairs**, without approval or execution.
- Input estimates: **9,721 / 21,374 / 21,482 / 21,579 / 21,558 / 21,581 / 21,519**.
- Cumulative estimate: **138,814**; final prompt: **47,977 bytes**.
- **19 catalog page reads**, versus 20 in the retained sequence.
- **22 contract resolution attempts**; three could not resolve because no exact
  receipt exists in the retained evidence. No replacement contract was invented.
- All **ten requested contracts** are shown in the final prompt and remain exact
  in the executable catalog. The mock resolves from this recording plus matching
  versioned receipts from the earlier review recording; it does not simulate
  unrecorded producer availability.

The older four-attempt review probe remains bounded: maximum **58,800 → 21,588**
estimated tokens compared with its historical unbounded context; every historical
attempt, including the unconfirmed one, remains retained. This is not a new live
performance claim. The binding-only repair probe still exceeds 24,000 when its
mandatory baseline/contracts are too large and stops safely.

## Designer and validation boundaries

**Inspect discovered tools** reads tenant-owned encrypted request snapshots. It
shows actual historical contract/index inclusion, source coverage, contract
sizes, additions/removals, saved input limits and calls. Current inspection
selections are separate from historical presentation and actual TaskPlan use.
Historical ranking scores and precise budget-exclusion reasons were not recorded
and remain unknown. Chat-owned sessions may lack historical request snapshots.

**Copy discovery report** exports allowlisted identifiers, tool names, counts and
diagnostic codes. It excludes descriptions, prompts, arguments, schemas, paths,
URLs and diagnostic/provider message bodies. Reading and copying dispatch no MCP
operation or inference. There are no manual selection controls.

Deterministic coverage includes whole-batch rejection, selection replacement and
clearing, recovery and accounting, mandatory overflow, unavailable/versioned
contracts, retained/far-tail visibility, tenant isolation, clipboard redaction,
all eight business scenarios, scoped repair and approval checks. Full solution,
frontend, Python, package and published AOT checks accompany this change; exact
revision and CI results are recorded in draft PR #113.

Local validation: **3,387 .NET tests passed across 33 projects**, including 463
planner and 467 host tests; five existing opt-in live tests remained skipped.
The latest host display changes also passed the full host suite. **318 Python /
script tests**, the Agent frontend build, five independent Flow package builds,
the benchmark host build (not execution), and the published macOS ARM64 Native
AOT smoke passed without warnings.

The Windows Cmd timeout/failure blocker and real Copilot sandbox execution
limitation remain separate. No live reliability or external-success claim is
made. Deploy the updated planner/Designer, then start a new planning session and
explicitly approve its reviewed workflow. Existing stopped sessions, approvals,
limits, accounting and historical evidence are not rewritten or resumed.
