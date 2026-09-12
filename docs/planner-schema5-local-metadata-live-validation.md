# Progressive Schema-5 live validation after local metadata resolution

The campaign stopped after Stage 1 returned an **unjustified clarification**.
Its question asks about omission behavior already specified by the frozen input.
This does not satisfy Stage 1 acceptance. Stages 2 and 3 were not run.

The local metadata fix passed preflight and enabled four completed generation
requests through the KeyVault-configured model. There was no provider, metadata,
sizing, truncation, or unverifiable-request failure in this run.

## Frozen campaign

- Production: `5034d52d2637b1b20df2bdc60c387aa0f23f583a`.
- Harness: `ee6383611ffaa20f959a3a62792220e73ea7fe38`.
- Campaign: `schema5-local-metadata-20260912`; tenant `planner-progressive`.
- Model: `OpenAi` / `gpt-5.5-2026-04-24`, loaded from KeyVault and Agent's saved metadata.
- Every request used `low`; production reasoning defaults were unchanged.
- Limits: 12,000 input / 8,192 output, 9,600 estimated-input dispatch target,
  concurrency four, five repairs per workflow/gate, and existing global budgets.
- Automatic background advancement was disabled. Exactly one fresh session started.

The encrypted campaign manifest uses `agent-planning-progressive-campaigns-v5`.
The isolated EF index is workspace-resolved under
`.GnOuGo/data/planner-progressive/schema5-local-metadata-20260912/gnougo-planning-v5.db`.
The manifest records binary hashes and scenario, catalog, fixture and policy
fingerprints. The complete redacted manifest, accounting and replay result are in
[the machine-readable report](planner-schema5-local-metadata-live-report.json).

The original `schema5-ee487c8` campaign, database and evidence remain unchanged.
It is available through the read-only `campaign archived-report` command.

## Results

| Measure | Stage 1 — standalone classifier | Stage 2 — batch | Stage 3 — CodeReview |
|---|---|---|---|
| Typed outcome | `NeedUserClarification`, rejected during benchmark review | Not run | Not run |
| Persisted status / revision | `clarification` / 15 | — | — |
| Verifiable model calls / reservations | 4 / 4 | — | — |
| Unverifiable dispatches / missing receipts | 0 / 0 | — | — |
| Decision pages | 4: three intent, one clarification | — | — |
| Engine-resolved executable decisions | 0; other engine decisions unknown | — | — |
| Model decision IDs | 18 distinct issued IDs covered by receipts | — | — |
| Model executable decisions / hole exposures | 0 / 0 | — | — |
| User clarifications / answers | 1 / 0 | — | — |
| Repairs | Intent 0; clarification 0 | — | — |
| Largest estimated / actual input | 2,540 / 1,673 tokens | — | — |
| Actual input / output tokens | 4,966 / 847 | — | — |
| Independent generated-workflow execution | Not reached | Not run | Not run |
| Approved artifact hash | None | None | None |

Session: `f29bac6fef454004943321e7688efc73`. The three intent requests consumed
4,778 input / 710 output tokens; the clarification request consumed 188 / 137.
All pages completed without corrections or splits. No behavior or executable
construction occurred. Decision IDs count model-exposed interpretation units,
not a claim of 18 validated business operations.

The retained classifier callee previously passed independent execution. This
standalone live planning attempt was a new test, preserving the approved caller's
optional threshold default of 100. It did not produce an executable workflow.

## First blocker and deterministic diagnosis

The complete source sentence is:

> Optional input threshold is a non-nullable number defaulting to 100 when omitted.

The second intent receipt classified the first part as `business_input` and the
final words **“when omitted.”** as a separate `business_choice`. The coordinator
assigned that fragment obligation `ob_50ccefe57e887f15`, backed by reference
`r_9da9ff26e41160941afa3b0a` (request offset 222, length 13).

The clarification request received that fragment as its business-decision context.
It asked: “What should the system do when the relevant value is omitted?” Its
alternatives were to proceed using default behavior or reject the omission. The
first is already required by the supplied default; this is not an unresolved
user-owned choice. No answer was invented or submitted.

The root cause is **clarification eligibility based on a model classification and
source ownership without proof that a business decision is still missing**:

- [PlanningSourceDecisions](../src/GnOuGo.Flow.Planning/PlanningSourceDecisions.cs)
  validates issued references and constructs obligations from the selected kind;
  the accepted `business_choice` label establishes the owner mechanically.
- [PlanningClarifications](../src/GnOuGo.Flow.Planning/PlanningClarifications.cs)
  considers unanswered business-choice obligations, checks that their source is
  user-owned, then prompts with only the selected fragment. It does not establish
  that the surrounding accepted evidence has left a decision unresolved.
- The source span is authentic, but it omits the neighboring default that answers
  the question. Distinct alternatives and a valid response schema do not prove
  that clarification is necessary.

Reviewer finding: `AUDIT_UNJUSTIFIED_CLARIFICATION`, canonical obligation location
`/obligations/@ob_50ccefe57e887f15`. This is an audit label, **not a production
diagnostic code**. The planner persisted no technical stop or validation finding;
its report and session remain unchanged in their actual clarification waiting state.
The campaign has not passed Stage 1, so the subsequent stage gates remain closed.

Receipt-only replay from revision 11 reproduced the same clarification in one
advance, replaying one completed receipt with zero provider dispatches. It verified
unchanged live state, journal rows and budget. The redacted campaign report also
remained identical before and after replay.

No `medium` A/B request ran. The erroneous intent receipt contains six interpretation
units; it is not the harness's eligible single failed behavior/semantic-review
decision. The generated question itself passed its current validation gate. There
is no evidence here that changing reasoning would repair the eligibility check.

## Validation boundary

Before live dispatch, the full offline solution suite passed **2,990 tests**, with
one unrelated opt-in live test skipped and zero failures or build warnings. The
harness built with zero warnings/errors. Six retained classifier/batch reference
families and all 18 frozen CodeReview fixture contract checks passed without model
or business-transport calls. These are prerequisite checks, not successful execution
of a newly generated workflow.

After observing this failure, only offline inspection, receipt replay and reporting
were performed. No production code, scenario, request ceiling, reasoning profile,
session answer, or frozen binary was changed. No new session or replacement attempt
was launched, and no final approval was granted.
