# Format-11 review replacement

This is a deliberate internal planning-format break on PR #120. Public MCP execution contracts, the semantic TaskPlan, compiler v15 JavaScript/YAML lowering and execution journal schema 9 remain unchanged. Old encrypted planning records are retained, but cannot be resumed, revised, approved or saved through the current planner. Starting a fresh session is required. Financial inspection and permanent-abandonment checks read historical accounting data without executing an old planner.

## Removed mechanisms

| Removed | Replacement |
| --- | --- |
| Presentation-version 2–11 predicates, historical response readers and monolithic proposal generation | One current format-11 session and exact current issued-schema recovery |
| `PlanningBusinessJudge` and its reference/optional-enable paths | One mandatory business verdict path |
| `PlanningConstructionReviewCoverage` and incremental conservation implementation | Exact host-owned review receipts in existing checkpoint journals |
| Proposition acknowledgments, subject/broad classes, dependency conservation classes and review windows | Coherent business blocks; all obligations in the final global review |
| Local/intermediate duplicate flags, interval settings and overlapping dispatches | Pending business review on technical checkpoints; one suitably scoped dispatch |
| Whole-workflow proposal correction and separate commitment/output correction coordinators | Existing scoped typed patches, candidate replacement and retained defect witnesses |
| Global-intent shortlist ranking, automatic full-catalog inclusion and legacy navigation aliases | Immediate-step discovery, exact cached contracts and bounded navigation |
| Optional built-in `workflow.plan.business_judge` input | Mandatory business review; unknown input rejected by the current built-in schema |
| Tests exclusive to retired acknowledgments, windows, version fallbacks and one-call monolithic planning | Current lifecycle traces plus retained pure compiler/runtime and authority regressions |

Full MCP contracts remain stored intact. The Judge's default projection contains actual behavior and business meaning, not contract schema trees or a replacement coverage manifest. Hashes, source/contract versions, tenant/session identities, receipts and replay guards stay in host code. Explicit requested originals remain retrievable and must fit admission; no new review paging fallback exists.

At implementation commit `d2aa76f7`, the tracked change comprises 378 files, 7,800 additions and 42,855 deletions. Production alone changes 89 files with 1,766 additions and 7,820 deletions: **6,054 net lines removed**, including eleven deleted production files. Sixty-five test files devoted to retired behavior are deleted; current behavioral and pure compiler/runtime fixtures replace relevant assertions. These counts exclude the separately appended evidence.

## Preserved safeguards and behavioral replacements

Current tests exercise several fragments receiving one business-block review, one global review for a small completed workflow, mechanical checkpoints without dispatch, and context retrieval followed by supported reinterpretation of the unchanged plan and evidence. Actual repairs still consume corrections. Missing context and unsupported findings do not grant repair authority or approve a fragment.

The retained compiler/runtime fixtures continue checking original record provenance, nullable values, isolated group arguments, ordered duplicate-preserving calls, required assertions, cleanup after failure, exact exports, executable transform instructions, v15 output and large graphs. Static fixtures explicitly claim neither model compliance nor approval. Current host fixtures separately exercise ordinary session creation, global review, artifact approval, encrypted storage, exact request recovery and tenant boundaries.

Independent runtime oracles, artifact-bound delegated review and individual invocation permissions remain execution prerequisites. A clean Judge is not execution authorization.

## Measurement policy

Line deletion demonstrates removed code, not business convergence. Completed live traces must establish calls, bytes, tokens, costs, latency and oracle results. Historical results retain their original source revision. Received invalid reports, output exhaustion, input admission failures and provider uncertainty are distinct outcomes; none is silently retried or counted as efficiency.

The current six-run qualification replaces the older eighteen-comparison gate: two consecutive intervention-free successes per scenario on one final source, both Collections variants each time, with each Collections run below 26 logical calls, at most four Judges and at most 13,197 complete Judge-request bytes. PR #120 stays Draft until qualification passes.
