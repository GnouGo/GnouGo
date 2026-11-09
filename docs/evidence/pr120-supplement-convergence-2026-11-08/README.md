# Existing-campaign supplement and targeted convergence

Current audited state [after Code17](continuation-300-state-after-code17.json):
EUR 1,200 campaign ceiling, EUR 217.19535 available with every retained liability.
Code and Amazon remain unqualified end to end. Full validation on 2e610242 found
three return-navigation regressions; it is [recorded as failed](validation-2e610242-failed.json).
The [corrected original-basis access](batch-original-basis-correction.json) passes
all 2,931 planning tests, and the Copilot task clarification passes all 245 MCP tests.
New full validation remains pending. The latest complete passing local snapshot is
[8,548 passed on d52e07a0](validation-d52e.json). Earlier snapshots below remain historical.


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


[Two consecutive Native runs](native-frozen-pair.json) qualify the unchanged
numeric scenario on frozen `0f3028a2` and the same profiles. Each used five
planning calls, accepted executable behavior at call 4, received favorable global
review, then passed artifact-bound delegated approval, actual execution and the
unchanged independent oracle (`result = 42`). Planning cost estimates were EUR
0.3841827138378862516793551276 and EUR 0.3799641737572772055530676219. The evidence
includes captured invocation time and cost through each first acceptance. No
external calls or uncertain holds were added. These results precede the next
presentation change and do not prove the accepted-block review boundary.

[Compact-JSON exact-request probes](compact-json-exact-probes.json) changed only
system-message content (+166 UTF-8 bytes), preserving the complete original JSON
context, strict schema, reasoning, allocation and all other actual HTTP fields.
Code's formerly incomplete request returned valid JSON using 94 output tokens;
Amazon's returned a valid draft extension using 1047 output tokens. Both took one
physical attempt and applied no candidate or business effect. This supports a
fresh workflow trial, not causality or a completed business outcome. Syntax
guidance is scoped to fresh Discovery and Draft; pending requests retain their
exact bytes and Strict admission may omit optional guidance without displacing
original context or changing the existing ceiling. Unrelated phases retain their
existing presentation. Contract-materialization guidance remains diagnostic-only.


The full solution on clean frozen `0f3028a2` completed with **8,380 passed and
12 platform/opt-in skips across 33 assemblies**, with no test failure and warnings
as errors. Frontend production assets were built before browser tests; the
clarification browser case was enabled through the actual Playwright module. The
actual Native AOT smoke and Browser Release publish passed on that source. These
results precede the latest syntax/retention changes; final validation remains
required on the resulting source. All **2828 planning tests** pass with the
scoped compact-JSON change and diagnostic materialization helper.


[Collections01 and its exact group-selection probe](collections01-and-group-selection-probe.json)
expose a separate semantic loop: four control reselections omitted the already
offered closed group's `groupId`, so drafting correctly excluded unselected calls.
One earlier reusable-group contribution was accepted; the remaining work did not
converge. The protected stop cancelled this experiment at logical call 26. Its 25
physical calls have known usage; the pending call had no physical HTTP attempt
but retained a Server application reservation of EUR
1.2505418719211822660098522167. Exact Server/ledger/cancellation evidence now
supports permanent retention for that race, with the full separate hold intact
and both replay and continuation forbidden. The new regression failed before
correction; all **219 retention tests** pass, including cold restart, exact-hold
checks and rejection of active state or a missing cancelled result. Separate
external reservations are now EUR 3.6721182266009852216748768472, counted once.

The independent probe changed only system-message content (+481 UTF-8 bytes).
The original schema, final JSON context, medium reasoning, 4096 output allocation
and all other actual HTTP fields were identical. It selected the available
`foreach` plus `double_element` group association in one schema-valid attempt,
without applying a candidate or executing anything. Generic fresh selection now
presents that association when an existing closed group is available and this
is not completion-only selection. The selected group and every call still require
exact interface validation. Original context and Strict admission remain intact;
**12 focused syntax/identity/association tests** pass. Ordinary fresh workflows
and final full-solution/CI validation remain required after this change.

## Frozen successful cohorts and subsequent contract failures

[Native and Collections qualification](frozen-qualified-native-collections.json)
records two consecutive fully qualified runs of each on `abe71c6d`, with both
Collections input variants executed in each run. The proof includes artifact-bound
delegated review, real execution, independent oracles, and measured time/cost to
first accepted contribution. These results do not qualify Code or Amazon, and do
not prove the exact unchanged-plan accepted-block review boundary from `bcc0434f`.

[Code06's exact evidence-selector comparison](code06-object-exact-comparison.json)
keeps all other HTTP request fields fixed. Both object-selector probes completed
within the unchanged output allowance; no candidate was applied. Fresh discovery
uses that equivalent encoding while historical issued requests recover unchanged.

[Amazon05's execution failure](amazon05-context-and-provenance-failure.json)
retains a globally reviewed, approved workflow that actually searched, visited
products, wrote its workbook and cleaned up, but failed qualification. Its three
requested product URLs were absent from all observed search hrefs. The final
reporting inference also exceeded the provider's physical context capacity.
Neither fact is an oracle defect. The full uncertain HTTP reservation remains
held; context rejection does not establish zero billing. Generic physical-capacity
preflight now remains hard under diagnostic warnings, and fresh drafting guides
consumers toward necessary structured inputs while retaining complete observations
at their producers. The separately authorized exact-observed-link extension and
v5 oracle remain strict; v4 and business requirements are preserved.

[Code07](code07-stop-and-materialization-probe.json),
[Code08](code08-stop-and-prerequisite-probe.json), and
[Code09](code09-stop-and-negative-contract-probes.json) preserve protected stops,
permanent retention and actual controlled probes. Code08's technically accepted
metadata-echo task is explicitly not useful business progress. Its first guide
probe changed both guidance and reasoning; the subsequent low-reasoning repeat
verified a single guidance insertion against identical remaining HTTP fields.
Code09 selected Git clone and CodeAgent together, but lacked lifecycle dependency
contracts; prompt-only bundle and identity presentations did not acquire them.
Those variants remain diagnostic. All three cancelled workflows forbid resumption.

`4a77d503` adds exact metadata inspection before executable binding through the
existing `discoveryRequests` and durable continuation. It checkpoints each verified
contract, preserves source snapshot ownership and accepted work, and resumes cold
without inference or duplicate reads. Unknown, denied, ambiguous and cross-source
identities are rejected before reads; failed exact contracts retain protected
metadata recovery. Default navigation stays bounded to displayed or explicitly
inspected identities. Optional menu presentation yields to Strict input targets;
the complete offered action schema and original evidence remain available. Draft
expression details stay in drafting rather than being repeated during discovery.

Validation on `a955474f` completed all 33 solution test assemblies: 8,457 passed,
12 skipped, zero failures. Its three PR CI workflows passed. On `4a77d503`, the
200 focused discovery/contract tests, production frontend build, warning-free
Release benchmark/tool build, Core/Planning packages and actual published Native
AOT smoke passed. Full-solution validation and fresh live qualification are recorded
separately when complete; earlier successful cohorts are not final-source proofs.

## Final continuation: retained Code progress and financial admission

[Code10](code10-inspection-progress-and-reselection-failure.json) used the new
metadata-only action and resolved six contracts, but accepted no business work.
Its protected stop retained the pending unsent application reservation. The live
run exposed a legal same-source contract batch rejected as duplicate source reads;
this is now covered by a failing-then-passing regression. A replacement cursor
also restores verified contract/schema evidence from its existing rejection
journal for the same block, scope and accepted baseline. Cross-scope, changed,
denied and corrupt evidence gain no reuse.

[Code11](code11-composed-binding-and-native-serialization-detour.json) acquired
seven contracts in its call-6 batch and selected a composed group, but accepted no
work. It identified the chosen writer's base64 restriction and premature cleanup,
then searched for an external JSON serializer after resolving document_write.
Discovery compaction had removed the native json_encode cue. The compact cue is
restored without changing the response grammar or introducing an encoder MCP.
This was an observed omission and detour, not a copied-request causal proof.

[Code12](code12-accepted-work-and-financial-boundary.json), on d060e6ca, accepted
its first executable contribution at call 25, observed after 10m43.477s and
EUR 9.94506 of measured planning usage. It retained two technically accepted
checkpoints and received an ordinary local business-review verdict of ok at call
30 after cleanup context was inspected. The following code-review responsibility
reused the accepted qualification work. These are planned behaviors, not executed
observations. The local review followed a changed fragment and does not establish
the exact bcc0434f unchanged-plan review boundary.

Request 35 stopped before HTTP dispatch: its current physical reservation was
EUR 7.93435, exceeding the EUR 5.85615 available in the existing campaign.
The existing unstarted-request retention path binds its exact request, refusal,
empty HTTP journal, configuration and accounting. Replay and run continuation are
permanently forbidden; every old charge and reservation remains intact. There is
no global review, artifact approval, real Code execution or Code oracle success.

[Final campaign accounting](final-campaign-funding-summary.json) records the
single 400-to-800 EUR supplement, EUR 467.30222 of known usage tariff estimates,
EUR 310.83681 of unresolved HTTP reservations, EUR 16.00482 of separately retained
application reservations, and EUR 5.856148678907299596954765142 available. These are
campaign accounting values, not provider invoices or a global funding statement.
No further grant, Unlimited mode or release of uncertain funds was used.

The complete business objective remains unfinished: the exact unchanged-plan
review boundary is unproved; Code and Amazon have no qualified success; the earlier
Native/Collections pairs are not a requalification of the final source. No new
Amazon full run was financed after these corrections. Browser fixtures and the
separately authorized strict redirect extension retain their existing evidence.

The final functional source is f2e31340. The CI cursor-guidance failure was
reproduced and fixed by restoring the explicit selected-scope wording, retaining
the original assertions and Strict limits. All 2,890 planning tests passed.
The full solution completed all 33 assemblies: 8,481 passed, 12 skipped and
zero failures. The production frontend, warning-free Release benchmark/tools
build, independent Core/Planning packs, and actual published osx-arm64 Native
AOT smoke passed. [Final validation](final-validation.json) retains the source
and hashes of the private logs. The twelve existing skips remain explicit.
Superseded interrupted full runs are not counted as passes. CI status is checked
separately; local success does not imply a remote CI conclusion.

[Remote CI snapshot](ci-final-functional-source.json) records both ProxyCopilot
checks passing on f2e31340. The deterministic planner workflow was still running
its integration step at the final inspection; no remote pass is inferred from the
local full-solution success.

## Additional continuation authorized 2026-11-09

The user authorized another EUR 100 for the existing campaign.
[The verified supplement](supplement-100-once.json) raises its ceiling from EUR 800
to EUR 900 exactly once under `pr120-supplement-20261109-100-01`. Prior accounting
and all retained reservations are unchanged. The audited available allocation
after registration is EUR 105.856148678907299596954765142. Old permanently retained
runs remain forbidden; continuation uses independent fresh sessions.


The targeted review-boundary evaluation first exposed an already-bound output
forcing a redundant draft. [The failing run](boundary01-bound-output-first-cause.json)
and its failing-then-passing regression support the narrow unchanged-binding fix.
[A later trace](unchanged-plan-review-live-proof.json) observes an ordinary local
Judge approving unchanged accepted work, with no new task, scope or requirement
closure and no draft echo. Its build began before the source commit, so this is
not a frozen-source qualification. The targeted workflow then stopped on an
inconclusive global request for planning history; it has no execution success.
Code13 preflight rejected stale MCP build metadata before session creation or
model dispatch. A clean post-commit rebuild restored source-bound admission.

[Code14](code14-qualification-progress-and-host-failure.json), on 9f9affa3, retained
seven checkpoints and an ordinary successful local review of repository
qualification. Its first pinned-clone contribution arrived at call 36, after
15m54.606s and EUR 17.58645 of measured planning usage. The initial pending-report
setup is not counted as qualification. Total measured planning tariff was about
EUR 50.19090, with no global approval or execution. This is slower and more
expensive than Code12's earlier partial progress; full Code convergence remains
unproved. After the known call-71 receipt, a suspended consumer's metadata check
rendered a contract view without a current cursor and caused a null dereference.
The exact immutable pre-failure snapshot reproduces it offline; the identity/hash
correction prepares the next request without inference or persistent writes.
All 182 focused tests and 2,897 planning tests pass with the correction. Code14
has 71 known receipts and no pending model request; it was not resumed.

[Amazon06](amazon06-missing-policy-metadata.json), on 2debe960, exposed a separate
benchmark-context omission: the prompt required the approved route policy without
presenting its configured route set or host enforcement guarantees. It accepted
no work. The protected stop retained the unsent call-22 application reservation;
there was no HTTP attempt for that request. Replay and continuation are forbidden.
The prompt now includes the same route pattern used by the spawned Browser and
explains its existing enforcement. It grants no new route, operation, runtime fact
or execution authority. All 172 focused host/oracle/approval tests pass, including
the reproduced missing-route regression. No expected response or plan was injected.
The audited available allocation after this retention is EUR 41.60762, with
EUR 19.75462 of separately retained application reservations. No hold was released.

[Intermediate full validation](intermediate-validation-9f9affa3.json) completed
33 assemblies on 9f9affa3: 8,501 passed, 12 skipped, zero failures. This is
historical validation; later source revisions require their own results.


The [exact unchanged-plan boundary](unchanged-plan-review-frozen-proof.json) is
now proved on frozen 72f17ca7: an ordinary
local Judge returned ok after the accepted contribution, on the identical plan
hash, with no new tasks, scope/requirement closure or drafting echo. The root was
open. The targeted protocol evaluation was stopped after that prefix; it is not
an executed workflow or end-to-end qualification. Its cancellation exposed a
Server reservation before the campaign envelope had been created. Paid work was
blocked while a narrow conservative retention path was implemented. The real
immutable snapshot passed its preview and was permanently retained with its full
EUR 6.147756381549485 application hold. Before retention, the corrected audit
rejected the unretained hold; afterward it included the hold in available-funding
calculations. All 589 benchmark/financial tests pass. Original budgets,
reservations, permissions, replay controls and the approved route policy remain
unchanged; the correction creates no request or response evidence.

[Amazon07](amazon07-recovery-prerequisite-loop.json), with source-bound a99828b0
tools and the unchanged v5 oracle, accepted search/cleanup and product occurrence
selection. The ordinary local Judge approved that search block at call 14. It
then sought universal continuation after raised errors and partial-workbook
delivery, although the originals require truthful errors, missing data and
cleanup without stating those additional recovery guarantees. Repeated discovery
accepted no further work. The protected stop permanently retained uncertain call
50 and its full EUR 4.3041737572772055530676220331 campaign reservation; all 49
known receipts remain intact. Measured planning tariff was EUR 24.482445141065830721003134806.
There was no global approval or execution. This run consumed too many calls to
establish improved convergence or early loop detection.

Fresh generic planning and review guidance now distinguishes successful
observations with missing data from raised failures and explicitly requested
recovery. It preserves explicit recovery requirements, existing cleanup and
failure semantics, original text, schemas and ordinary business review. It
creates no catch control and chooses no verdict. Strict input targets retain
their existing limits; optional guidance is omitted when it does not fit.
Already issued requests remain exact. The correction has not received a fresh
live qualification. All 2,906 planning tests pass, including nine new checks for
fresh guidance, unchanged authority/verdict choices and strict input limits.

[Intermediate a99828b0 validation](intermediate-validation-a99828b0.json) completed
one unfiltered full-solution invocation: 8,514 passed, 12 skipped, zero failures
across 33 assemblies. The subsequent guidance correction is validated separately.

[The latest campaign audit](campaign-funding-summary-20261109.json) confirms the
EUR 100 supplement exactly once and a EUR 900 ceiling. Available allocation is
EUR 5.640214957456336766681589158 after campaign charges/reservations and separately
retained application holds. These are captured-tariff estimates and conservative
holds, not provider invoices; global available funding remains unavailable. No
further long paid qualification was started. Full Code/Amazon convergence and
the requested final-source consecutive success cohorts remain unproved.

[Complete local validation on 57f6b4f6](full-validation-57f6b4f6.json) passed
8,523 tests across all 33 assemblies, with 12 existing skips and zero failures.
The slow workbook fixture ran in four [disjoint method groups](test-partitions-57f6b4f6.json)
against the same frozen binaries: 216 inventoried cases, each included once.
The remaining solution invocation excluded only that fixture and passed 8,307
tests. The superseded partial invocation contributes no results. The production
frontend, warning-free Release benchmark/staged tools, independent Core/Planning
packs and actual published osx-arm64 Native AOT smoke also pass. These local
fixtures do not establish autonomous live qualification.

[Remote CI on that functional source](ci-functional-source-57f6b4f6.json) has both
ProxyCopilot checks passing. The main planner integration workflow remains in
progress; its pass is not inferred from the local results. Validation records
name the functional source independently of subsequent documentation commits.
PR #120 remains Draft and unmerged.

The next user continuation authorizes another EUR 300 for the same campaign.
[The verified registration](supplement-300-once.json) raises its ceiling from
EUR 900 to EUR 1,200 exactly once under `pr120-supplement-20261109-300-01`.
Prior charges, the authorization history and all reservations remain intact.
Available allocation immediately after registration is
EUR 305.640214957456336766681589158; global funding and provider invoices remain
unavailable. No old permanently retained run is resumed.

The subsequently completed main CI run on ff66905a failed the protected discovery
recovery browser smoke with HTTP 409 instead of 200 after explicit resume. A
controlled regression reproduces read-only request/discovery inspection holding
the short OS lease while an ordinary local command is submitted. Inspection now
uses the existing local command gate, allowing the command to wait for that read.
Active workers and cross-coordinator conflicts retain their original lease,
journal, cancellation and financial protections; no retry or permission is added.


The EUR 300 continuation preserved the same strict Amazon extension and v5 oracle.
[Amazon08](amazon08-first-cause.json) accepted search/cleanup and occurrence selection,
then passed its ordinary local search-block review at call 14. The next responsibility
propagated raised failures with required cleanup instead of inventing universal recovery.
A later cursor nevertheless saw an empty optional argument projection for a previously
used Browser operation. Exact-version, unchanged accepted-producer input selections
are now reusable; values and binding authority are not copied. Cancellation also
exposed an overly narrow one-uncertain-attempt retention predicate. Retention now
verifies every earlier sibling journal, completed receipt and conservative coverage;
it preserves all money and forbids old replay. All 595 focused benchmark tests pass.

[Amazon09](amazon09-first-cause.json), on b480b5fa, selected the same isolated
search group twice despite having the connected contracts. Both drafts rejected
that strategy because it would close a resource still needed by later consumers.
The offered schema already contained composed controls. The run was stopped at
call 22 with 21 known receipts and no accepted checkpoint; it never reached global
review, approval or execution. Its complete uncertain reservation remains retained.
The subsequent generic guidance distinguishes business review, group declaration
and invocation, composed operation selection, and ancestor-owned cleanup. Four
runtime cases verify success, consumer failure, producer failure and an unused group;
100 focused checks and all 2,919 planning tests pass. Guidance is optional under
strict targets, including the complete request identity; historical requests stay exact.

[Intermediate validation on b480b5fa](validation-b480.json) covers the complete
solution: 8,537 passed, 12 existing skips, zero failures, 33 unique assemblies.
The 216 slow fixture cases ran once each in four disjoint groups against frozen
binaries. [The preceding 3f38b833 validation](validation-3f38.json) is retained
separately. None establishes live end-to-end qualification. After Amazon09's
permanent retention, the campaign audit showed EUR 268.459319301388266905508279476
available, with EUR 33.673622928795342588446036724 of separately retained
application reservations. Global funding and provider invoices remain unavailable.


[Code15](code15-first-cause.json), on 6c0fdf07, accepted the pinned clone,
Copilot qualification and root cleanup at call 15 (about 619 seconds to its receipt,
EUR 5.4813882669055082848186296462 measured planning tariff). Its ordinary local
Judge approved the qualification block at call 18 with the root still open and the
later review/report explicitly pending. The fixed business responsibility remained
unchanged. At implementation-selection call 22, the provider returned 555 characters
of incomplete JSON followed by 16,103 formatting-whitespace characters, exhausting
its unchanged 4,096-token allocation. No fragment was accepted from that response.
All 22 receipts and charges are known, with no unresolved transport reservation;
measured planning tariff totals EUR 9.117635467980295566502463056. This is not global
review, approval or execution. The old run will not be resumed. The generic correction
applies the existing compact JSON presentation to fresh implementation selection,
without increasing allocations or changing response choices; 66 focused checks pass.
Available campaign allocation after the run is EUR 259.341683833407971339005816276.

[Complete local validation on 6c0fdf07](validation-6c0f.json) passed 8,546 tests,
with 12 existing skips and zero failures across 33 unique assemblies. Every slow
fixture case ran exactly once against unchanged frozen binaries. The production
frontend, warning-free Release/staged tools, both independent packages and actual
published Native AOT smoke passed. This source precedes the implementation syntax
correction and is not relabeled as its validation. Remote standalone and Native AOT
CI passed; the planner workflow's external Copilot checksum download returned HTTP
504. One failed-job rerun was requested on the unchanged commit.


[Code16](code16-first-cause.json), on d52e07a0, accepted the pinned clone,
repository observations, Copilot qualification and declaration inspection. The
ordinary local Judge returned ok at call 27 after one needs_context response,
with the root still open. The first contribution arrived at call 19 (about
756 seconds to its receipt, EUR 7.8240483654276757725033587103 measured planning
tariff). This is slower and more expensive than Code15's first contribution;
no convergence improvement is claimed. Its implementation decisions completed
without the former output-exhaustion incident, but the second block accepted
no executable work before the protected stop at pending call 37.

Call 30 selected four legal issued evidence IDs. Reading the next interface page
first moved the cursor and caused the later original-input schema lookup to reject
its previously visible accepted producer. A controlled regression reproduces that
order-dependent failure. The correction retains the already issued host producer
page before cursor advancement, without registering an extra requested read or
binding authority. Ten new cases cover both batch orders, exact automatic description
continuations and atomic rejection of denied, stale or missing schemas; all 347
focused checks pass. Complete validation of this correction remains pending.

All 36 known Code16 receipts remain intact, with measured planning tariff
EUR 17.0035378414688759516345723241. Its full uncertain application reservation
EUR 8.613210927004030452306314375 is permanently retained; replay and continuation
are forbidden. The [dated campaign state](continuation-300-state.json) reports
EUR 233.724935064935064935064929401 available after all captured campaign charges,
campaign holds and separately retained application holds. The EUR 300 supplement
remains registered exactly once. No global review, approval or execution success
is inferred from the local Judge.

[Complete validation on d52e07a0](validation-d52e.json) passed 8,548 tests,
with 12 existing skips and zero failures across 33 unique assemblies. Frontend,
warning-free Release/staged tools, both independent packages and actual published
Native AOT smoke pass. This validation precedes the batch-cursor correction.
[Remote CI evidence](ci-external-checksum-504.json) records the repeated official
Copilot checksum-download HTTP 504, including a single failed-job rerun and an
independent read-only reproduction. The standalone and Native AOT checks pass;
planner CI is not reported green. PR #120 remains Draft and unmerged.


The automatic producer-page snapshot introduced a distinction that the ordinary
navigation presenter did not preserve: cached metadata was mistaken for a
model-requested read. Three existing return-navigation tests failed locally and
in remote CI on 2e610242. All 216 slow fixture cases passed, but the full result
was 8,555 passed, 12 skipped and three failed, and is not counted as a pass.
The [replacement correction](batch-original-basis-correction.json) evaluates all
producer access against the immutable state from before the batch, including
exact description continuations. It retains only requested evidence, commits
ordinary cursor changes and leaves rejected state unchanged. No automatic page
or successful-read marker is stored. The existing three tests remain unchanged;
369 focused checks and all 2,931 planning tests pass.

[Code17](code17-first-cause.json) repeatedly rejected the compatibility Copilot
wrapper because contract discovery did not guarantee runtime command availability.
No executable work was accepted. It was stopped at pending call 31 after 30 known
receipts (EUR 14.2283027317510076130765785937 measured planning tariff). Its full
EUR 2.3012807881773399014778325123 application reservation is permanently retained;
old replay/continuation is forbidden. This is not improved convergence.

The existing wrapper already delegates to Core's bounded coding operation and
retains observed commands/terminal results, but its metadata and opening prompt
prescribed editing. They now describe inspection, review, checks or requested
edits, subject to the actual SDK tools and unchanged permission/sandbox policy.
Unavailable commands and absent observations remain explicit; session completion
does not establish successful checks. No tool, permission, allocation or oracle
was added. Two regressions preserve read-only task text, unchanged files, truthful
unavailability, the same interactive permission mode and one disposed session.
All 70 focused and 245 MCP tests pass; no fresh live success is inferred.
