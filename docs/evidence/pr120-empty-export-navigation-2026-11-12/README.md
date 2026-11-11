# Empty accepted-export navigation

## Demonstrated cause

Frozen `8f2d02f4` run `amazon-v13-qualification-20261111-01` reaches six technical
checkpoints and two favorable local business reviews. During implementation
selection, calls 41–44 request the same unavailable export page through their
distinct, correctly issued short IDs. The original `/root` directory says zero
accepted exports and an empty names array, but still offers the link in both
context and response schema. The reader intentionally returns no accepted export
page for an empty scope. This is a directory/reader mismatch, not a missing
operation, stale model-authored ID or transport cutoff.

[Sanitized issued-request evidence](live-reproduction.json) preserves the request
identities and empty directory facts without business payloads or credentials.
The normal stop API cancels the attempt at revision 116. It is not replayed.

## Generic correction

`AcceptedExportDirectory` keeps the empty accepted declaration's zero count and
empty names, and offers an export-page link only when at least one export exists.
The ordinary scope link remains available. Existing nonempty export validation,
frontier/version checks, candidate closure checks and non-consumable status are
unchanged. No interface, import, repair, operation or execution authority is added.
Historical issued requests, responses and diagnostics are not regenerated.

Two regressions first fail on the empty link, including cold restoration. A third
checks that the directory's nonempty link still retrieves the exact original
schemas and remains non-consumable. All **2,981 planning tests pass** in Release
with `-warnaserror`, zero failures and zero skips. The earlier shared baseline is
9,090 passes and 13 existing skips; shared checks for this correction remain pending.

## Live and qualification boundaries

[Stopped-run metrics](stopped-run.json) contain 45 issued calls, 44 complete
physical HTTP attempts, known usage on all 44, maximum HTTP duration 92.391120
seconds and EUR 21.0917062248096730855351545002 captured-tariff cost. The first
useful checkpoint takes 10 calls, 508.030057 seconds from run start and EUR
2.9567935512763098969995521718; it establishes search/cleanup planning only.
Global review, approval, execution, workbook and independent oracle are not reached.
Full Amazon remains **0/2**, full Code **0/2**.

The cancelled final request has an empty physical-attempt journal. Dispatch remains
unknown; its entire application reservation, EUR
4.9073065516311674305742787814, is kept separately. Retention preview binds the
exact cancelled request and source. The first apply is refused by the existing
clean-worktree guard; no retention or financial release is inferred from that
refusal. Administrative application follows the tested commit. No supplement,
reset, Unlimited or historical reservation release is authorized.

## Separate generated-binding finding

[A zero-dispatch reproduction](input-lower-bound.json) combines only unchanged raw
bindings from the real-profile source preflight: 1,360 records and 28 own source
resolutions. The classifier's search plus full destination-page observations
already estimate 4,464,176 tokens against the declared 1,017,232-token physical
input allowance. The production admission check rejects this lower bound as
`LLM_BUDGET_EXCEEDED`, `request_input_allowance`, `not_started`. These are conservative
estimates, not consumed tokens or an actual workflow call.

The raw results occupy 8,953,887 UTF-8 JSON bytes; the 28 destination observations
contribute 7,699,330, while their original redirect evidence needs 120,216. Full
observations must remain at their producers. Before approving a fresh full artifact,
repair its existing receipt export with typed field/object projections carrying
the necessary original occurrence, redirect, acquisition/completeness, status and
provenance data instead of unrelated destination-page records. Validate every
affected consumer and business review again. Do not truncate observations, omit
occurrences, raise limits or reinterpret warning mode as physical capacity.

That scoped proposal was not applied to the cancelled session. Its correction and
real execution remain prerequisites for complete qualification on a fresh identity.
