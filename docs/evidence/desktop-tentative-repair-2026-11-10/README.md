# Tentative continuation repair

Base: `21932defd5f39c4969f4e226283a5e86a78f5ff0`.
Implementation: `51535690` (`fix(planning): retain tentative continuations for scoped repair`).
Warning preservation: `16d47f4a` (`fix(planning): preserve warnings from staged transitions`).

The reported Desktop incident is session
`ba1f62ae52eb4948ae41adc01a87ba5a`, trace
`af7a1ba666338dacf5f9f579c232bc08`, blocked at
`2026-11-10T18:29:41Z`. Implementation and tests below use synthetic records;
the stopped session and its requests, receipts and financial history are untouched.

## Demonstrated cause and correction

The earlier read-only diagnosis and detached reproduction established this chain:

1. The draft supplied a flattened header vector where an operation required a
   matrix. Its expression also omitted the collected data rows.
2. Continuation validation compiled the tentative plan to inspect previously
   selected imports. The unrelated owned consumer error cleared all compiler
   interfaces and threw before the coordinator retained its candidate.
3. The following request consequently had no candidate/draft context and requested
   a complete replacement. That response finished with provider output exhaustion
   at its issued 8,192-token allocation. Its HTTP/protocol completion did not make
   the truncated model content usable.

The coordinator now stages extension application, commitments and continuation
metadata on a clone. Envelope/authority failures still throw. Owned compiler
findings from the business continuation probe remain attached to the unaccepted
candidate and enter the existing typed repair flow. Full candidate validation
merges these findings without publishing invalid interfaces. The strict extension
validation helper retains its rejection behavior for other callers.

Nested item schemas and literal instance pointers improve diagnostic messages;
codes, semantic locations and compatibility rules are unchanged. Item presentation
has four recursion levels, four alternatives per union and a 512-character bound.
Exact contracts remain available through existing evidence retrieval. No public
API, stored-session format, repair version or inspector was added.

## Controlled verification

`TentativeContinuationRepairTests` uses generic prepared-row and table-operation
contracts. The first two tests were run before implementation and failed because
the candidate was null and the diagnostic omitted nested item shapes. Both pass
after implementation.

Review also demonstrated that advisory warnings recorded on the staging clone
needed to be copied back independently of publishing its transition. Two additional
red cases reproduced the missing warning on retained and rejected envelopes. The
follow-up preserves those observations in `finally`, using existing deduplication;
both cases pass without granting candidate admission or changing targets.

The 15 targeted cases cover:

- retaining the exact invalid candidate, baseline, diagnostic and technical history;
- offering the existing typed patch for the owned consumer binding;
- keeping accepted checkpoints unchanged and invalid compiler interfaces empty;
- compiling and executing the corrected candidate against an in-memory MCP sink,
  observing exactly one write containing the header and all ordered source rows,
  including duplicates, null cells and whitespace;
- restoring a source-generated pending request and serialized synthetic receipt
  through a fresh runtime without another simulated generation or duplicate
  correction accounting;
- rejecting output-limited repair content while retaining candidate, usage and
  correction accounting, with no continuation of the stopped test session;
- preserving the independent business-review gate for a type-valid header-only
  matrix (the controlled reviewer returns `needs_change`);
- rejecting wrong scopes, operations, identities, continuation envelopes,
  unavailable producers, stale baselines and edits outside issued slots;
- retaining advisory size warnings from the staging clone on both admitted and
  rejected envelopes, without publishing a rejected candidate or its commitments;
- exposing literal instance pointers only in messages and bounding deep item
  schema descriptions.

These responses and the business reviewer are deterministic test fixtures. The
table sink is an in-memory MCP tool, not an Excel file or an Amazon source. The
receipt-restoration test establishes the coordinator contract with serialized
synthetic data; it is not a new durable provider receipt or a paid live test.

On frozen code `16d47f4a`, the complete solution build reports zero warnings and
errors. Shared validation passes 8,761 cases with 13 existing skips: seven Windows
command cases, five opt-in live Copilot cases and the known Designer clarification
browser case. All 2,967 planning cases pass; the 15 targeted cases are included in
that count. Release packaging and the existing published Native AOT smoke pass.

Server coverage uses the repository's existing partition runner: all 2,066
discovered cases occur exactly once (2,065 passed, one known skip), with no missing
or extra cases and unchanged binary fingerprints. Its 217 workbook fixtures all
pass. The first unfiltered shared run was cancelled after the follow-up changed
source; it is recorded as incomplete and excluded from the final counts.

Validation commands, final results and log fingerprints are recorded in
`validation.json`. The shared solution run excludes Server only because the
inventory-verified runner separately covers every Server case against the same
frozen build. Targeted reruns and native smoke are not added to shared test totals.

## Qualification boundary

The implementation establishes owned tentative repair and its rejection safeguards.
It changes no provider, endpoint, transport, model, output allocation, timeout,
retry policy, permission or campaign budget. No paid request, campaign amendment,
reservation release or original-session replay was performed.

No fresh full workflow has been accepted, approved or executed against the original
Amazon environment, and no independent live oracle has qualified it. That requires
a fresh admitted session, complete delivered responses, ordinary full validation
and business review, explicit execution authority and the unchanged independent
oracle. A shorter repair response reduces required regeneration; it does not
guarantee future model completion or business convergence.
