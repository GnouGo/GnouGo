# Bounded discovery and exhausted-session recovery

This pass preserves TaskPlan, the compiler/runtime, planning format 10, execution
journal schema 9 and the existing model/token ceilings. Baseline: `87043b3`.
Implementation: `2d78f06` (planner) and `2017b7a` (Designer).

## Retained failure

Session `e8d3a029c2fb48b8b95e9a7bca37f54a` returned eight confirmed discovery
responses: 28 pages, 85 distinct capabilities, no TaskPlan and no repairs. Its
saved call ceiling was eight. Subsequent token-setting changes restarted the
exhausted session and appended duplicate findings without dispatching another
call. Raising per-request token limits could not extend the cumulative call limit.

The sanitized fixture in
[`retained-discovery-exhaustion.json`](../tests/GnOuGo.Agent.Server.Tests/Fixtures/DiscoveryBudget/retained-discovery-exhaustion.json)
retains all eight responses, original issued requests/schemas, contracts,
accounting and revision history. Repeated catalog/page payloads are represented
once; each pending checkpoint records its original page count and resolved IDs.
Private repository URLs and local user paths are redacted. The original encrypted
records were read through KeyVault APIs and verified unchanged.

## Correction

- Before the first TaskPlan, discovery leaves one proposal call plus the remaining
  repair allowance. Defaults therefore permit five discovery calls, followed by
  one proposal and up to two repairs. A repair's last attempt cannot request more
  discovery. Smaller allowances retain a proposal opportunity; other cumulative
  budgets can still stop earlier.
- Newly issued prompts show remaining calls and repairs. Their strict schemas
  enforce closed discovery. A null plan then means `DISCOVERY_INCOMPLETE`; a
  forbidden discovery response means `DISCOVERY_NOT_ALLOWED`. Neither result
  authorizes a workflow, metadata fetching or another automatic repair.
- Historical requests retain their issued schemas and identities. A previously
  issued sixth discovery request can still replay; a newly issued sixth request
  cannot use the same discovery response. Existing reservations are never reset.
- The global Top-8 ranks retained candidates across pages, preserving relevance
  weights, refinement and stable ties. Current compact indexes remain available
  for widening. Exact selected contracts remain mandatory; historical full pages
  and unselected contracts stay out of prompts. Conflicting versions fail closed.
- Designer shows calls used / ceiling, deduplicates displayed findings and disables
  ineffective token-setting/revision restarts. Server validation rejects those
  commands atomically. Viewing an old session never rewrites its findings or usage.

## Deterministic evidence

The initial focused run exposed 13 failures; the retained replay separately
reproduced the unbounded sixth discovery response. The initial UI regression
failed because the ceiling and disabled action were absent. All logs are retained
under `artifacts/bounded-discovery-2026-09-27/`.

The corrected focused suites pass 438 planner tests and 461 Agent.Server tests.
Coverage includes original-response recovery, bounded discovery/proposal/repair,
explicit inability to plan, forbidden actions, cumulative budgets, earlier-page
candidates, conflicting versions, revision conflicts, unchanged approvals and
Designer recovery. Existing business oracles, the two-source scenario's two
scripted calls and metadata-cache assertions remain intact. Python/script tests
pass (318), and the Agent frontend builds without warnings.

Frozen coding/review context, including response schemas:

| Historical attempt | Input estimate before | After | Prompt bytes before | After |
| --- | ---: | ---: | ---: | ---: |
| 1 | 9,210 | 9,312 | 7,716 | 8,022 |
| 2 | 18,344 | 18,040 | 35,037 | 34,123 |
| 3, unconfirmed | 20,743 | 20,574 | 42,232 | 41,725 |
| 4 | 20,743 | 20,574 | 42,232 | 41,725 |

The largest generation request remains below the existing 21,000-token test
threshold. Cumulative estimates fall from 69,040 to 68,500. TaskPlan JSON remains
16,344 bytes; complete response JSON remains 16,378 bytes. These are deterministic
presentations, not replacement inference or new live performance evidence.

The synthetic full-plan binding repair grows from 24,487 to **24,599 estimated
input tokens** with the budget explanation and response contract. It still
exceeds 24,000 and must stop: mandatory contracts and the baseline are not trimmed.

Full solution, packaging, published AOT/persistence and exact-revision CI results
are recorded in [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). The
pre-existing Windows Cmd timeout and real Copilot sandbox/command-execution
limitation are separate; this change does not resolve them.

## Recovery and limits

Deploy the updated planner and Agent.Server, then create a new planning session
and explicitly approve any resulting workflow. The exhausted session remains
inspectable with its original requests and accounting. No budget extension,
automatic restart, paid inference, live benchmark or external workflow is part
of this pass. The fix guarantees bounded proposal opportunities and safe stops,
not that a live model will find every needed capability or produce a valid plan.
