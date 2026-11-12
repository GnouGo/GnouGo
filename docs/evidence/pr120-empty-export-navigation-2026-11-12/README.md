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
with `-warnaserror`, zero failures and zero skips. The earlier shared baseline was
9,090 passes and 13 existing skips.

The completed shared check at `745a12fc` records **9,110 passes and 13 existing
skips**: 6,721 Release non-Server passes across 32 projects and 2,389 Server passes
with exact partition coverage and unchanged binaries. Builds use `-warnaserror`;
the repository Server partition runner uses Debug. These overlapping sets are not
added to the separate planning count. See [shared validation](shared-validation.json).

A [fresh independent probe](live-fix-probe.json) removes the 26-byte empty-directory
link from the exact call-41 context, retaining its original schema, low reasoning
and 4,096 output tokens. It completes in 44.406465 seconds with known protocol/usage,
at EUR 0.7889744738020600089565606807 captured-tariff cost. The evidence-only response
requests five other original addresses. On the exact issued baseline/map, the
production reader resolves all five and admits their complete follow-up without
changing the plan or accounting. Fresh production follow-up schemas also omit the
unavailable empty-export address. This probe deliberately retains the historical
response schema, including its unused old ID; it is not a full new planning session
or a useful executable fragment. No source session is resumed or changed.

## Live and qualification boundaries

[Stopped-run metrics](stopped-run.json) contain 45 issued calls, 44 complete
physical HTTP attempts, known usage on all 44, maximum HTTP duration 92.391120
seconds and EUR 21.0917062248096730855351545002 captured-tariff cost. The first
useful checkpoint takes 10 calls, 508.030057 seconds from run start and EUR
2.9567935512763098969995521718; it establishes search/cleanup planning only.
Global review, approval, execution, workbook and independent oracle are not reached.
Full Amazon remains **0/2**, full Code **0/2**.

A [read-only audit of the first ten calls](first-checkpoint-http.json) measures
the retained actual request bodies, prompt/schema projections, sent reasoning and
first nonempty output separately. All ten send `low`, complete with known usage,
and individually take at most 51.410101 HTTP seconds; their HTTP durations total
411.288201 seconds. The largest retained body is 120,071 UTF-8 bytes. Three calls
request evidence before continuing. These measurements establish delivery within
the proxy constraint, not a complete accepted workflow or a demonstrated defect
in those legitimate retrievals. No requested contract evidence is dropped to
force a smaller call count.

The cancelled final request has an empty physical-attempt journal. Dispatch remains
unknown; its entire application reservation, EUR
4.9073065516311674305742787814, is kept separately. Retention preview binds the
exact cancelled request and source. The first apply is refused by the existing
clean-worktree guard; no retention or financial release is inferred from that
refusal. Administrative application follows the tested commit. No supplement,
reset, Unlimited or historical reservation release is authorized.

After the tested commit, permanent retention is applied successfully at
`2026-11-11T23:09:04.0975930Z`, with evidence hash
`31b7cd3285bc1481ad2b6e24b62417ad050d041181e3920adc325d5ec74c0f01`.
The entire reservation remains; replay and continuation are forbidden. The
[post-retention audit](financial-summary.json), before the independent probe,
retains all six grants, the EUR 1,700 ceiling and all 50 historical unknown HTTP
attempts. Its conservative application-hold projection is EUR
78.429543670458514476397434969. Provider invoices and global funding remain unavailable.

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
