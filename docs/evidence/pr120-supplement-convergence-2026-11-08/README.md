# Existing-campaign supplement and targeted convergence

The explicit authorization `pr120-supplement-20261108-400-01` increases only
campaign `pr120-live-20261107-400` from EUR 400 to EUR 800. The existing leased
`extend-budget` operation was invoked once after checking that this reference
was absent. [The verified ledger](supplement-once.json) contains exactly one
matching entry. The original grant, 601 physical attempts, known charges, all
22 unknown attempts and every reservation are unchanged. Immediately after the
operation, EUR 402.9363636363636363636363616195 was available after HTTP holds
and the separate external reservation. This is an allocation balance, not
provider invoicing or established global funding.

The first regression reproduced an existing incompatibility: fresh-allocation
admission required the current ceiling to equal the original grant. The generic
correction accepts only the existing continuous supplement-authorization chain,
with a unique reference and exact previous/next ceiling for each entry. It keeps
the immutable original allowance and reports the current authorized ceiling
separately for new Server sessions. Restart/idempotency, retained uncertainty,
admission above the old ceiling, hard refusal above the new ceiling and malformed
chains are covered. All **183 focused campaign tests passed** without warnings.

The [baseline planner CI run](https://github.com/GnouGo/GnouGo/actions/runs/42482140139)
failed five interactive browser tests: it skipped the frontend build and never
produced the required helper assets. The CI correction installs the pinned pnpm
and Node versions, installs the frozen workspace lockfile with strict peers,
then builds Agent.Server ClientApp before browser tests. Shared UI sources and
root package/lockfile changes now trigger that workflow. The actual production
frontend build and all **eight focused startup/diagnostic/clarification browser
tests passed locally** afterward, including delayed and missing-asset cases.
This local verification does not claim a subsequent remote CI pass.

The targeted live trial uses a fresh real code-review workflow. Its exact
unchanged-plan business-review transition must be observed before claiming the
`bcc0434f` boundary exercised; successful arithmetic is not evidence of it.
Cancelled historical sessions are retained and cannot resume.

The first targeted trial on `868a2868` stopped at discovery call 14 with no
accepted contribution. [Its original streams and exact-request comparison](boundary01-and-discovery-guide-comparison.json)
show two native transport endings about 300 seconds after their last bytes,
before both captured 600-second deadlines. The existing owner admitted one
independent attempt, retained both full uncertain holds and stopped at its
allowance. Exact permanent retention preserves all originals and forbids old
continuation/replay. This run does **not** exercise the business-review boundary.

An independent protected Server probe changed only HTTP message content, adding
a **724-byte compact required-field guide**. Every other wire field, strict
response grammar and original context remained identical. It returned a valid
response in one physical attempt, 16.35 seconds through its saved report, at
EUR 0.2479265562024182713837886252 in captured-usage tariff estimates. No candidate
or business tool was applied. This supports testing field visibility, not a claim
of causality, an upstream diagnosis or time/cost to an accepted contribution.

Fresh discovery requests use this guide instead of a full prompt copy of the
grammar. Diagnostic captures retain it even when an advisory input hint is crossed.
Ordinary Strict hosts omit only optional guidance when the whole request would
cross their existing maximum; all context, contracts and hard admission stay intact.
This boundary corrected a newly reproduced strict follow-up regression, without
raising a limit. **169 focused planning tests passed**, including discovery,
retained recovery and business-block completion. Final solution validation remains
required after live work stabilizes.

The generic Browser correction passes **157 component tests**, including controlled
ARIA/native occurrences with multiple links, duplicate destinations, page order,
actual final-document/response provenance, cookies, forbidden host/path redirects,
loops, the physical redirect ceiling and non-replayed POST interactions. The
component's Release publish also passed. No Amazon permission or oracle was
changed at that stage. The [separate scope record](amazon-scope-proposal.md)
now records the subsequent explicit user authorization. That authorization alone
cannot be used as a successful Amazon qualification.


## Subsequent Code and Browser evidence

The [second Code workflow and exact delivery probe](code02-and-delivery-probe.json)
record 60 completed physical attempts, eight accepted checkpoints and three
favorable local business reviews. The first technical contribution was accepted
at call 18; local reviews completed at calls 25, 34 and 55. The last local milestone
cost EUR 31.6640662785490371697268248983 through 55 calls. The original requirements
remained open for final closure. Global review requested an extra caller return
although the originals prescribe file delivery, and the sealed interface could
not grant that new declaration. The run stopped with an explicit technical
authority question, without approval or execution. No exact unchanged-plan empty
business-completion claim was selected, so the bcc0434f route is still unproved
live. Distinct interface/task pages at calls 42–49 were new bounded evidence;
there was no identical-strategy repeat to justify cancelling that useful work.

The compact Discovery guide was actually present in HTTP calls 5–11. A separate
probe of the exact retained global-review request changed only system-message
content; all other wire fields, context and strict schema were identical. It
returned `ok` in one physical attempt for EUR 0.6594088669950738916256157635.
This is supporting diagnostic evidence, not an applied candidate or a qualified
workflow. Generic delivery-channel guidance now preserves specifically requested
caller returns while distinguishing file delivery; 60 focused planning tests pass.
A fresh workflow is required after these production changes rather than bypassing
the retained execution source/tool guards.

[Subsequent Browser qualification](browser-and-strict-amazon-scope.json) records
165 passing component tests and actual complete Amazon observations, including an
exact observed sponsored link with real 302→200 responses. Controlled regressions
cover pending navigation, escaped queries, history versus HTTP identity, nested
variant associations and denied child frames. No inference was used for these
reads, and the browser closed afterward. The separately authorized strict scope
and v5 verifier retain v4 history and every workbook/field/cleanup requirement.

The [later CI run](https://github.com/GnouGo/GnouGo/actions/runs/42495265576)
built the frontend successfully, then failed local workbook cases on the escaped
query boundary (`%20` became whitespace internally). The generic URI correction
was independently reproduced and checked in Browser tests; the affected V15
workbook baseline passes locally afterward. Both ProxyCopilot validation jobs
passed. A final remote run and final full solution validation remain required.


The strict-scope/verifier and host regressions pass **176 tests** without warnings,
including unchanged v4 tests, exact observed-entry authorization, expiration,
forbidden routes/hosts/ports, missing or inconsistent references, order, redirects,
aliases, query, scope, completeness, exact workbook fields and read-only metadata
startup. Metadata approval/revalidation now preserve the actual configured Code
endpoint rather than injecting a placeholder, and share the initial discovery
policy digest so an unchanged source is compared consistently; no catalog check
is waived.


The [third Code trial](code03-whitespace-loop.json) stopped at discovery call 15
without an accepted checkpoint. Its single independent retry generated a short
JSON prefix followed by tens of thousands of whitespace characters, retaining no
complete value or usage receipt. This identifies generated-content non-progress;
new stream bytes alone are not useful progress. The auxiliary protected stop host
failed before startup on a missing private native dependency; the existing owner
then stopped at its captured deadline. Both full reservations remain, and the
existing exact-evidence retention permanently forbids replay/continuation. The
available campaign balance after all holds is EUR
335.5778325123152709359605886795; no further supplement was applied.

CI on `395b0cce` separately reproduced a Browser fixture race (server arrival can
precede delivery of Playwright's request event) and an HTTP restart diagnostic
that overwrote a fired total-generation deadline when UTC had not advanced.
The fixture now synchronizes both facts. A deterministic frozen-UTC regression
failed before the generic deadline-latch correction; **378 AI.Core tests** and
**20 Browser lifecycle tests** pass afterward. Release packing and the actual
Native AOT planner smoke pass, including source-generated retention of the fired
deadline and partial bytes. The strict v5 verifier also rejects unresolved mixed
primary heading aliases; **28 v5 selection tests** pass. Final full-solution and
remote CI results remain pending after these changes.


[Two separate exact-request probes](code-discovery-output-allocation.json) isolated
syntax guidance and output allocation. The syntax-guidance probe changed only
system-message content and ended without a receipt; its full hold is retained
and it is permanently non-replayable. The guidance remains diagnostic-only.
A separate probe changed only `max_completion_tokens` from 32768 to 4096, retaining
all original prompt bytes, strict schema, context and medium reasoning. It returned
schema-valid JSON in one physical attempt, 82 output tokens and about 16 seconds,
for EUR 0.2774294670846394984326018809. No candidate or effect was applied.
This supports a fresh live trial, not a causal or qualified-convergence claim.
The new `server-navigation4096-draft32k.json` changes only navigation allocation;
Draft stays 32768/low and Review stays 8192/medium. Every requirement and oracle
remains unchanged. Browser's published field descriptions now explain nearest
groups, outer semantic items, heading association and history versus HTTP identity
without assigning business meaning to records.


The [smaller-allocation workflow and equivalent-order probes](code04-and-equivalent-array-order.json)
show the distinction between bounding waste and useful progress. Code04 returned
a complete receipt at call 10 with `length`: only 59 non-whitespace characters
were produced before repeated whitespace filled the 4096-token allocation.
Nothing was accepted or executed. All ten calls have known usage; no new uncertain
reservation was created. Increasing allocation is not authorized by this result.

Two subsequent independent probes retained that exact prompt, original context,
reasoning, 4096-token allocation, strictness and every schema constraint. The only
wire difference was permutation of `evidenceRequests.anyOf`, placing the existing
one-element alternative before the unbounded alternative. Both returned valid
JSON in one attempt (94 and 86 output tokens). This does not establish a provider
root cause or causal convergence. The generic fresh-Discovery preparation now
uses this equivalent presentation after alias assignment and before final identity,
measurement and admission. Pending calls retain exact historical bytes. All
**2823 planning tests pass**, including semantic-equivalence negatives, untouched
literal/example/extension data, scoped repair and issued-request recovery.


[Code05 and Amazon04](code05-amazon04-and-cancelled-retry.json) used the same
frozen source `0f3028a2` and Navigation 4096 / Draft 32768 / Review 8192 profile.
Code05 had 29 completed calls, no accepted contribution and another output-limit
whitespace failure; all usage is known (EUR 7.8955217196596506941334527537).
Equivalent array ordering did not eliminate this failure. Amazon04 reached Draft
at call 8, then its ninth call lost one native stream and generated a partial
draft followed by repeated whitespace on the independently admitted retry. The
protected operator stop succeeded with zero additional provider dispatches. No
artifact was accepted, approved or executed. Both full reservations remain; exact
permanent retention forbids request replay and cancelled-session continuation.

The cancellation revealed a missing benchmark retention case: an already
journaled independent generation before the cancelled native stream. Its new
regression failed before the correction; all 215 retention tests pass afterward,
including rejection of received completions, changed retry authority and
overlapping or missing timestamps. The correction creates no retry authority and
changes no historical charge, partial or hold.

A separate protected probe added only 717 UTF-8 bytes explaining that binding
materializes the exact selected contract before drafting. Actual HTTP comparison
confirms every other field and the original final JSON context were identical.
It returned valid JSON but selected the same description reread as the original
call; no improvement to accepted contribution is demonstrated. This guide remains
diagnostic-only. The first separate-worktree readiness attempt dispatched no
inference; its assets were built before the fresh probe identity.

After Amazon04, before subsequent trials, the ledger retained 29 unknown attempts
and all historical holds. Available allocation was EUR
300.5721988356471115091804719795 after the separate external reservation. The
additional EUR 400 authorization still appears exactly once. These results do not
establish an upstream root cause or the exact bcc0434f business-review boundary.
