# Scoped prerequisite repair and capability gaps

PR #108 / issue #110 extends the existing semantic planning pipeline. It adds no transport or MCP tool, and no provider-specific planner rule.

## Reported failure

Session `3d9bd1a1302646a6a0c279cee9fd63fd` lacked authoritative external comparison metadata before repository preparation. Another action required the repository artifact that the blocked preparation could not produce. After six of eight calls, the old repair path installed a changed semantic plan before discovering that complete grounding and binding needed more calls. The persisted semantic, grounding and binding state could therefore disagree.

The original session remains stopped at revision 13 with six calls. Verification reads its encrypted journal through public KeyVault APIs; it does not modify that session.

## Changes

- Optional structured prerequisite diagnostics identify missing observations, missing artifact producers, unavailable outcomes and dependent blockers. Issued action scopes, selected consumers, contract paths and root references are checked deterministically. Nested actions belong to their issued binding container. Root links cannot form cycles or hide independent blockers.
- Technical failures never create planner decisions. Scoped semantic repair receives existing contracts, complete catalog explanations and accepted boundaries. It can insert an observation producer; an opaque response still needs runtime validation before fields are consumed.
- A repair checkpoint retains the candidate separately from the accepted plan. Exact scope and semantic/catalog/binding fingerprints are checked, unchanged siblings and reusable coverage are preserved, and only the valid accepted binding prefix survives dependency invalidation. Budget feasibility is checked before atomic installation. Failed transitions retain the previous coherent state and expose the unapplied repair.
- A capability gap takes the semantic repair path even when an accepted binding prefix exists. Interactive business clarification shows a concrete proposed scope change and requires acceptance, rejection or cancellation. Auto stops without accepting reduced requirements. Mode changes never grant consent. Ordinary preferred decisions continue to require valid implementations of the agreed requirements.
- Designer and originating Chat share scope-revision and repair details, including answered history. Chat recovery reopens only the saved planner under its existing journal and ownership lease; submission acknowledgement follows persistence.
- The protected publisher itself declares that findings appear in one review body and inline comments are unsupported. The planner has no special knowledge of publisher names, URLs or domain phrases. No generated workflow is approved or executed by these changes.

## Budget and compatibility limits

Default limits remain eight model calls and two replans. Remaining-work estimates account for reusable catalog coverage, known selections, binding batches and known outstanding scenario requests. They are lower bounds: later choices, scenarios and repairs may require more calls. Saved repairs reuse reservations and receipts; restart neither repeats a completed model call nor resets usage. Additive schema-8 fields preserve existing sessions, constructors and reserved request schemas.

FinalReview, artifact approval, runtime permissions, protected publication confirmation/fresh-head checks, and runtime `human.input` retain their existing boundaries. An inline publication request cannot be treated as satisfied by a review body without explicit scope consent.

## Verification

Deterministic suites cover renamed generic producer/consumer capabilities, opaque observations and invalid values stopping before consumers, independent and dependent failures, precise sibling/prefix preservation, the six-used/eight-allowed atomic failure, checkpoint replay, stale fingerprints, scoped consent/rejection/cancellation/mode changes, originating-chat restart and existing approval protections.

- Flow.Planning: 269 passed.
- Flow: 890 passed.
- Flow.Integrations: 79 passed.
- Agent.Server: 404 passed.

The warning-free solution/frontend builds, planner packaging, Native AOT planner smoke and trimmed Server encrypted persistence smoke passed.

Local real-model verification uses the configured OpenAi `gpt-5.5-2026-04-24` model and Chromium browser UI. Supported metadata-reading workflows reached FinalReview with passing simulated scenarios in Designer and Chat, in Auto and Interactive modes. The full original request was also copied without removing its inline requirement; unsuccessful attempts are retained in the evidence rather than represented as successful generation.

The Designer verification host explicitly configured a 24-call allowance for newly created test sessions; the two-replan and per-request limits stayed unchanged. This does not alter product defaults or the original session's eight-call allowance. Chat's workflow-owned sessions retain their own request limits. Private prompts, responses, drafts and session payloads remain encrypted. Only sanitized identifiers, counts and findings are included in [verification evidence](evidence/planner-prerequisite-repair-2026-09-23.json).

| Supported case | Mode | Result | Model calls | Passing scenarios |
| --- | --- | --- | --- | --- |
| Designer metadata observation | Auto | FinalReview | 6 | 5 |
| Designer metadata observation | Interactive | FinalReview | 5 | 5 |
| Chat metadata observation | Auto | FinalReview | 4 | 5 |
| Chat metadata observation | Interactive | FinalReview | 4 | 7 |

These four saved grounded plans and their complete review artifacts revalidate on the final code. No workflow approval or execution was performed. The scenarios use simulated external responses. Real-model results demonstrate supported paths; they do not establish that the complete original request can satisfy its unsupported inline-publication requirement.

## Capability gaps and restart

A fresh Auto copy of the original full request stopped after four calls with `NONE_OF_THE_ABOVE` on publication and an unapplied scoped revision. The requirement was retained. Targeted Chat gap cases stopped in Auto or displayed explicit business clarification in Interactive after three calls. No ordinary preferred decision reduced the requirement.

After restarting Server, the originating Chat restored its pending scope card. Keeping the original requirement stopped that session, saved the rejection and restored its answered history after reconnect, without another model call. In Designer, the saved Auto explanation was visible; switching to Interactive exposed the same proposal without consenting or regenerating it. Cancel planning retained the proposal and call count. HTTP-level tests separately verify accepted and rejected scope answers through the originating Chat endpoint.

The larger Interactive copy (`bd4d602ae0944596a70f2815982aba7f`) checkpointed a technical repair at call seven, targeting `a5`/`a6` and inserting observation action `a4b`. It preserved the earlier accepted work and reached the publication batch. That model response mixed blockers with executable work and was rejected. After its second repair, the remaining binding request exceeded the unchanged 12,000-token input limit. The session stopped after 13 calls and two replans; it did **not** reach FinalReview. Its inline requirement and approval boundaries were preserved. This is a remaining generation-size limitation, not evidence of successful full inline review generation.

The final solution/frontend builds were warning-free; the planner package, Native AOT smoke and trimmed Server persistence smoke passed. Saved review artifacts revalidated, and recovery actions did not increment model-call counters. PR #108 remains open for review and closes issues #107, #109 and #110 when merged.
