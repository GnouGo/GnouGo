# Compact TaskPlan convergence

## Source `08c5229fc`: three received diagnostic failures

None of these runs produced an artifact or executed. PR #120 remains Draft;
the three-scenario acceptance gate and 18 paired comparisons remain unmet.

| Frozen case | Logical requests / HTTP attempts / receipts | Corrections / checkpoints | Planning seconds |
| --- | ---: | ---: | ---: |
| [Collections](compact-taskplan-08c5229fc-collections.json) | 25 / 26 / 25 | 2 / 6 | 923.630 |
| [Amazon](compact-taskplan-08c5229fc-amazon.json) | 48 / 51 / 48 | 4 / 6 | 2,619.593 |
| [Review PR](compact-taskplan-08c5229fc-code.json) | 47 / 48 / 47 | 4 / 3 | 1,652.013 |

Collections claimed its full requirement complete while exporting only the foreach
body value. Its Judge correctly distinguished that local export from the required
public array. The repair repeated the rejected draft exactly and stopped for
non-progress. The existing `reselectStep` route can withdraw the premature claim;
focused version-5 and exact-recovery tests confirm that route, not live efficacy.

Amazon's [independent assessment](compact-taskplan-08c5229fc-amazon-assessment.json)
confirms a genuine zero-capacity transform array: the repair changed only
`maxItems: 0` to `3`, then passed review. This is separate from the earlier false
finite-loop finding. Final selection invented an undeclared caller input and was
rejected. Paging removed direct navigation to earlier inputs/producers, although
`scope:/root` still offered the exact caller input declarations. Navigation causality is unproven.

Review PR's [independent assessment](compact-taskplan-08c5229fc-code-assessment.json)
confirms that its accepted clone lacks the required branch and exact workspace-relative
destination. That explicit restriction, `req_7`, was absent from the initial draft
and Judge context; fetching `req_6` did not supply it. Execution authority would reject
the clone. Later selections repeatedly proposed another clone despite retained rejection
feedback and accepted-task evidence. The final task exactly repeated the accepted ID
and arguments and failed identity validation. The accepted Copilot argument does consume
`reviewText`; no runtime use is claimed. Its dependency-wording finding was grounded but
semantically contestable, and its single executable-literal repair is not proof of an
avoided omission. [Original-text measurements](compact-taskplan-08c5229fc-code-formatting.json)
show all 47 responses matching saved JSON, with 14 whitespace bytes outside strings
and a maximum run of two.

The **2026-10-25 00:13:17 UTC** audit records **EUR131.53225418870898148713221314** known
spending and **EUR34.665325632635105129187028629** retained, with recorded upper bound
**EUR166.19757982134408661631924177** and **EUR73.83252401136221792612570553 available**.
All 28 receiptless identities remain closed; 48 unknown physical-attempt reservations
remain. No new abandonment was needed. Including the separately retained lost-ledger
exposure, the cumulative upper bound is **EUR326.16747598863778207387429443** within
the original EUR400 authorization. The archive preserves the CLR decimal rounding
qualification. This balance is a timestamped observation, not a future guarantee.

Exact `08c5229fc` passed **8,126 full-solution tests, zero failures and 13 skips**,
all **12 publication stages**, and all three CI workflows, with warnings as errors
and zero compiler diagnostics. The full-solution manifest hash is
`9bf21bdbb30ec3a77e07d2109b61387bb3c5237d8c9b8cdfadd78282b4436514`.
These results do not transfer automatically to later patches. No complete artifact
obligation is independently accepted here; calls per satisfied obligation remain
undefined. Phase sizes, provider tokens, costs and retained failures stay in the
immutable case archives.

## Source `afc8861e4`: all three diagnostics remain incomplete

No afc scenario produced an artifact or executed. PR #120 remains Draft; the
three-case acceptance gate and 18 paired comparisons remain unmet.

| Frozen afc case | Logical calls / attempts / receipts | Corrections / checkpoints | Outcome |
| --- | ---: | ---: | --- |
| [Collections](compact-taskplan-afc8861e4-collections-pending.json) | 2 / 4 / 1 | 0 / 0 | Response-less Transport; exact authorized closure subsequently applied |
| [Amazon](compact-taskplan-afc8861e4-amazon.json) | 44 / 45 / 44 | 4 / 4 | Prerequisite binding failed type check; correction allowance exhausted |
| [Review PR](compact-taskplan-afc8861e4-code.json) | 47 / 47 / 47 | 3 / 2 | Commitment refers to an undeclared operation result port |

Collections request 2 recorded HTTP 500, HTTP 500, then Transport without HTTP status
or receipt. Its [separate applied closure](compact-taskplan-afc8861e4-collections-retained-closure.json)
forbids replay and continuation, retaining all three reservations totaling
**EUR 2.7926104417670682730923694779**. The pending snapshot remains unchanged;
a missing receipt proves neither empty output, exhaustion nor timeout.

Amazon's [independent assessment](compact-taskplan-afc8861e4-amazon-assessment.json)
confirms the final type rejection. The readiness flag proved only current lineage
and a later checkpoint; relevant generation/review contexts omitted the retained
model-authored prerequisite type. Redundant accepted URL producers and these
presentation gaps do not establish why the model made its choices or predict a fix's benefit.

Code's [separate source assessment](compact-taskplan-afc8861e4-code-assessment.json)
confirms the undeclared-port rejection. The replacement chain used **3 of 3** local
corrections despite **3 of 4** global corrections; no missing fourth-correction route
is demonstrated. Prior technical feedback remained available. Two Judge findings treated
an operation's existing write-permission prerequisite as permission expansion, but
that inference is unsupported: effective `AllowWrites` was absent from the issued
context and remains unknown. No permission is inferred or relaxed. Code cost
**EUR 2.4283578759482373940205265507**, with no new reservation; no false-positive count,
avoided-correction or causal improvement is claimed.

The [original-format witness](compact-taskplan-afc8861e4-code-formatting.json), SHA-256
`ce38425d30b7a0d435c4e82d6b693312d2430b5ee2fd32e9c75423f7225e03b9`,
records all 47 texts parsing to their saved JSON, with 20 whitespace bytes outside
strings and a maximum consecutive run of three. Original-text hashes and parsed-JSON
presentation hashes are distinct; the earlier failed projection check is retained.

The **2026-10-24 22:17 UTC** audit leaves **EUR 84.27416613541398053210517899** available.
All **28 receiptless identities are closed**; reservations for **43 unknown physical
attempts** remain. The exact aggregate and separately retained lost-ledger exposure
are preserved; this timestamped balance is not a future spending guarantee.

Exact afc passed **8,105 full-solution tests, zero failures and 13 skips**, including
**2,961 Planning tests**, plus all **12 publication stages**, with warnings as errors
and zero compiler diagnostics. All **three exact-afc CI workflows passed**. Any
subsequent source patch and its tests remain separately attributed; the successor
has no live result, and these technical passes do not establish business acceptance.

The subsequent source change exposes the retained typed prerequisite to drafting
and review, restricts tentative result commitments to exact declared operation ports,
and consolidates Judge guidance about unknown prerequisites. It passed **281 focused
and 2,982 full Planning tests** (overlapping suites), with warnings as errors. The
original fixture failures and source-bound validation are retained. No changed
authority, extra correction allowance or live improvement is claimed.

## Source `40bd4a048`: Review PR remains incomplete

The [frozen Code diagnostic](compact-taskplan-40bd4a048-code.json) made **36 logical requests / 36 attempts / 36 receipts**,
with three corrections and two checkpoints. It produced no artifact and did not
execute. Planning took **956.271 seconds**; provider usage was **172,949 input /
41,040 output tokens**, including 31,932 reasoning tokens, costing
**EUR1.8705443998215082552431950022**. No new unknown reservation was added.

The first checks operation correctly consumed the accepted clone's directory port,
but asked a root-confined operation to write a sibling report. Both business findings
on that mismatch are supported by the original contract. Two replacements instead
bound a literal parent directory, which deterministic provenance validation rejected.
The intervening typed repair restored the correct producer port. Later business
correction context retained that business finding but lost the earlier rejected
literal and technical diagnostic. Technical repair context also incorrectly claimed
that original-contract validation had passed. These are demonstrated presentation
defects; their causal contribution to model choices remains unproven. The
[separate witness and audit supplement](compact-taskplan-40bd4a048-code-witness-supplement.json)
binds the exact observations without rewriting the measured archive.

All 38 constructed request identities reconcile: 36 dispatched and two prepared
only. Peak draft/Judge estimates were **13,924 / 10,053** tokens. No business success,
avoided correction or benefit from the higher draft ceiling is established. Calls
per independently satisfied artifact obligation remain undefined.
All 36 original response texts parse as JSON and match their saved JSON receipts.
They contain 12 whitespace bytes outside strings, with a longest run of two;
the later formatting witness is retained as SHA-256
`5a3eb5f86947a61b9ac7a0611af925369a3865e54a845c85e005da78ffe4a584`.

The **2026-10-24 19:46 UTC** audit reports **EUR119.69175887411728135326340460 known +
EUR27.012625944995658453595774726 reserved**, upper bound
**EUR146.70438481911293980685917933**, leaving **EUR93.32571901359336473558576797**.
All 27 receiptless requests are permanently closed; 39 unknown-attempt reservations
remain. The separately retained lost-ledger exposure remains accounted for.
The [exact authorized e6b Amazon closure](compact-taskplan-e6b24bb77-amazon-retained-closure.json)
is applied without releasing its reservation;
the earlier pending snapshot below remains historical evidence.

Exact `40bd4a048` validation passed **433 focused and 2,952 full Planning tests**
(overlapping suites), and all **12 publication stages**, with warnings as errors.
Full solution and final CI validation remain running at this snapshot. Technical
passes do not establish business acceptance; PR #120 remains Draft and the 18 paired
comparisons remain gated on all three candidate scenarios succeeding.

The subsequent correction retains bounded technical rejection feedback within the
existing replacement chain and removes the unconditional validation-pass statement
from fresh contract navigation. Its focused suite passed **189 tests** with warnings
as errors, including technical repair followed by business rejection, exact historical
values, stale support, bounds and historical versions. An initial fixture incorrectly
submitted a value forbidden by its response schema; that failed run is retained and
the fixture now exercises a type-compatible value lacking artifact provenance.
The production fix's effect on live convergence remains unverified.

## Source `e6b24bb77`: incomplete diagnostic results

Neither Collections nor Amazon produced an artifact or executed. Review PR has not
run on e6b at this snapshot. Three-case business acceptance and the 18 alternating
comparisons remain incomplete; PR #120 stays Draft.

| Frozen e6b case | Logical calls / attempts / receipts | Corrections / checkpoints | Planning seconds | Known EUR / retained reservation EUR | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| [Collections](compact-taskplan-e6b24bb77-collections.json) | 2/3/2 | 0/0 | 623.706 | 0.8970593485051316376617581437 / 0.9308701472556894243641231593 | Received output-limit exhaustion during intent extraction |
| [Amazon](compact-taskplan-e6b24bb77-amazon-pending.json) | 38/39/37 | 2/5 | 2,052.247 | 2.6069790272199910754127621594 / 1.4897054886211512717536813922 | Response-less Transport; exact retention authorization pending |

Collections request 2 received HTTP 500, then HTTP 200 with 32,768 output tokens,
all classified as reasoning, and zero response-content bytes. This is an empty
received response, not whitespace or receiptless uncertainty; its cause is unproven.
The failed HTTP 500 reservation remains retained despite the later receipt.

Amazon retains both an earlier HTTP 500 reservation and the full
**EUR0.5588353413654618473895582329** reservation for request 38, which has no HTTP
status or receipt. Its exact retention inspection is a preview: no permanent closure
or replay is authorized by that record. All 42 constructed identities reconcile as
38 dispatched and four prepared only, with no admission rejection. Peak draft/Judge
estimates were 17,582 / 7,949; the largest draft also fit the previous 18,000 ceiling.
No source, profile or convergence benefit is established.

The later [bounded navigation witness](compact-taskplan-e6b24bb77-amazon-navigation.json)
identifies the two charged corrections as explicit prerequisite suspensions at 23/36,
with no Judge-triggered correction. Selection 28 resumed the first consumer using
checkpoint 4 and the exact accepted string port; the accepted browser argument retains
that binding. The second continuation remains waiting. Discovery navigation at 18–21
and 32–35 repeated despite only one actual MCP discovery read in the run. These facts
establish neither avoided calls nor runtime correctness; the pending archive is unchanged.

The post-Amazon audit file preserved at **2026-10-24 18:59 UTC** reports
**EUR117.82121447429577309802020960 known + EUR27.012625944995658453595774726 reserved**,
upper bound **EUR144.83384041929143155161598433**, leaving
**EUR95.19626341341487299082896297** at that snapshot. Reservations for 39 unknown
physical attempts remain; 26 receiptless identities are closed and one remains
unresolved. No reservation is released by reporting these amounts. Later use changes
this balance; this is not a future allowance guarantee.

Two subsequent fresh-v5 source changes expose bounded cached operation choices for
selected accepted producers and dependencies, and consolidate interface-extraction
instructions. The instruction experiment preserves schema, context and settings.
Separate focused suites passed **358 and 75 tests**, and the combined focused suite
passed **433 tests**, with warnings as errors and historical/issued-request recovery
controls. Full Planning validation is running. No new live outcome or call/cost benefit is established. The e6b business
diagnostics remain incomplete and Review PR remains unrun.

## Source `f0644826b`: Code20 diagnostic

[Review PR Code20](compact-taskplan-f0644826b-code.json) stopped after **47 logical
requests / 47 attempts / 47 receipts**, four corrections and three checkpoints.
It produced no artifact and did not execute. Planning took **1,178.057 seconds**,
with **215,555 input / 48,582 output tokens** (37,438 reasoning tokens included in
output), costing **EUR2.2625925925925925925925925928**, with no new unknown reservation.
All responses were nonempty valid JSON; total outside-string whitespace was one byte.
Calls per independently satisfied artifact obligation remain undefined.

The first Judge identified the PR-page URL bound as the Git clone remote, and a
local repair supplied the frozen repository remote. This observation does not
isolate the guidance change's causal effect. A later Judge accepted a second
unconditional clone with identical arguments despite receiving the first clone's
complete arguments and the original single-clone obligation. The model subsequently
abandoned a third clone proposal; it did not repair the two accepted clones.

The final rejected consumer used the clone's nullable generic text output as
`projectRoot`, instead of its declared `workspace.directory` business port.
The provenance and type diagnostics are supported. `reviewText` was available but
not consumed by that rejected candidate; no scoped Judge assessed it. Producer-port
metadata was missing from the supplied interface navigation, a separate presentation
gap from the duplicate-clone false negative where the necessary evidence was present.

The four corrections were a replacement after duplicate prerequisite citations,
the remote binding repair, renaming a duplicate task ID, and abandoning the third
clone proposal. Exact repeated citations are mechanical navigation, not new evidence.
The subsequent fix normalizes them only after validating their original addresses
and raw bounds, and exposes retrievable original producer-port facts. Its business
effect remains unverified; no avoided-correction or duplicate-detection claim follows.

All 51 constructed request identities reconcile: 47 dispatched and four prepared
only, with no admission rejection. Peak draft/Judge sizes were **12,543 / 8,014**
estimated input tokens. The 20,000 draft ceiling was unused, so this run demonstrates
no benefit from raising it. Source and settings differed from the retained Code47
control; this is not an architecture-only comparison.

The post-run campaign audit retains **EUR114.31717609857065038494568929 known +
EUR24.592050309118817757477970175 reserved**, upper bound
**EUR138.90922640768946814242365946**, leaving
**EUR101.12087742501683640002128784**. All 26 receiptless identities remain permanently
closed; 36 unknown-attempt reservations remain. Exact aggregate rounding is retained.

Exact `f0644826b` focused validation passed 211 tests and full Planning passed
2,911 (overlapping suites). Its terminal full solution passed **8,055 tests, zero
failures and 13 skips** across 33 projects, with zero compiler diagnostics and all
4,534 source hashes unchanged. All **12 publication stages** and all **three CI
workflows** passed for that exact revision. These results do not cover later e6b
changes or establish live acceptance. Three-case business acceptance and the 18
alternating comparisons remain incomplete; PR #120 stays Draft.

## Source `47c08b428`: retained diagnostic results

The exact authorized [Collections `1b672ea17` closure](compact-taskplan-1b672ea17-retained-closure.json)
and Amazon `47c08b428` abandonment
are applied. Every original record and each full EUR0.9308701472556894243641231593
reservation remain retained; replay and continuation are forbidden. Their original
pending snapshots remain unchanged, with later closure evidence appended separately.

| Frozen 47 case | Logical calls / attempts / receipts | Corrections / checkpoints | Planning seconds | Known EUR / new reserved EUR | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| [Amazon](compact-taskplan-47c08b428-amazon-pending.json) | 2/2/1 | 0/0 | 389.681 | 0.1678134761267291387773315484 / 0.9308701472556894243641231593 | Response-less transport failure; [authorized closure applied](compact-taskplan-47c08b428-amazon-retained-closure.json) |
| [Review PR](compact-taskplan-47c08b428-code.json) | 31/31/31 | 0/3 | 758.077 | 1.3840383757251227130745203034 / 0 | Required cleanup generation request rejected: **18,131 > 18,000** estimated input tokens |

Neither run produced an artifact or executed. All 31 Code responses were nonempty,
valid JSON; the longest outside-string whitespace run was five characters. Three
local Judges returned no findings. Independent inspection nevertheless identified
a missed current argument defect: the accepted clone binds the pull-request URL
directly to `remoteUrl`, whose original contract requires a Git remote. The frozen
review included the original PR-URL obligation, exact binding and complete argument
description. Existing execution authority would reject the value before the MCP
call; no actual runtime failure is claimed. This does not establish an avoided
correction, and calls per accepted business obligation remain undefined.

The denied request has 53,625 serialized bytes, including 27,652 bytes of mandatory
contract evidence and 15,381 bytes of response schema. Components reconcile. It was
never dispatched; two other prepared-only identities are also separate from paid calls.
The next diagnostic tests consolidated argument-meaning guidance and a newly frozen
20,000-token draft ceiling together, with unchanged model/output/review settings.
Both changes must be disclosed; their individual effects are not isolated.

Post-run accounting retains 2,728 physical attempts, **EUR112.05458350597805779235309673
known + EUR24.592050309118817757477970175 reserved**, with retained upper bound
**EUR136.64663381509687554983106690** and **EUR103.38347001760942899261388040 available**.
All 26 receiptless identities are permanently closed; 36 unknown-attempt reservations
remain. Original aggregate rounding and source/profile attribution remain in the archives.
A stale historical authority-file hash in the freeze is explicitly qualified: the exact
source tree and validation inventory bind the current authority, and the six obligation
IDs, oracle behavior and execution guards are unchanged. The active freeze was not edited.

Exact `47c08b428` full-solution validation passed **8,054 tests, 13 skipped, zero failures
or compiler diagnostics**, with all 4,530 source hashes unchanged. All three exact-revision
CI workflows report success; inconsistent nested step metadata is retained separately
without asserting those individual steps passed. These technical results do not transfer
to later source or establish live convergence. PR #120 remains Draft; three-case acceptance
and the 18 alternating comparisons remain incomplete.

## Source `1b672ea17`: pending transport snapshot

[Collections stopped](compact-taskplan-1b672ea17-collections-pending.json) during intent extraction,
before any candidate fragment or Judge: **2 logical calls / 2 physical attempts / 1 receipt**,
zero corrections/checkpoints and no artifact or runtime. The second attempt recorded `Transport`
without an HTTP response; its output and provider termination are unknown, not an empty response.
The sole received response was valid JSON: 480 original bytes, zero outside-string whitespace.

Known cost is **EUR0.0150691655510932619366354306**, with the full
**EUR0.9308701472556894243641231593** unknown-usage reservation held. At the retained inspection,
**EUR105.86619201671697026882985541** remained available; exact abandonment authorization was
pending and no closure had been applied. Business coverage and calls-per-accepted-obligation are unavailable.
No efficacy conclusion follows for the unreached Judge guidance; three-case acceptance remains open.

Exact `1b` validation passed **3,047 tests** (2,910 Planning + 137 hosts) and **12/12 publication stages**,
with warnings treated as errors and no diagnostics; full-solution and CI results were pending at this snapshot.
A separate source audit identified benchmark-runtime transport/reservation gaps; this run made no runtime call and demonstrates no runtime billing error.

## Retained source `c17ee8a42` diagnostic snapshot

**Source `c17ee8a42` passed both Collections runtime variants; Amazon and Review PR stopped
without artifacts or execution. PR #120 remains Draft.** Three-case acceptance and the 18
alternating baseline comparisons remain unestablished. Detailed measurements remain in archives.

| Frozen c17 case | Calls / attempts / receipts | Corrections / checkpoints | Planning seconds | Known EUR / new reservation EUR | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| [Collections](compact-taskplan-c17ee8a42-collections.json) | 29/29/29 | 2/9 | 668.096 | 1.1009995537706381079875055776 / 0 | Both runtime variants passed |
| [Amazon](compact-taskplan-c17ee8a42-amazon.json) | 33/33/33 | 3/5 | 887.977 | 1.6682507809013833110218652388 / 0 | Mandatory Judge denied: **9,614 > 9,000** input tokens |
| [Review PR](compact-taskplan-c17ee8a42-code.json) | 23/23/23 | 3/2 | 606.626 | 1.2553369031682284694332887102 / 0 | `CONSTRUCTION_DEFECT_UNCHANGED`; no execution |

Collections has one parallel foreach calling a closed doubling group, a direct public result
and no alias. Nominal/alternate execution took **377.9249 / 4.019 ms**, both passing the unchanged
`cleanup`, `declared_effects` and `exact_outputs` oracles without effects or violations.
Useful progress is 29/3 calls per frozen oracle obligation; both corrections were deterministic.
Finite capacity 100, extreme numeric ranges and parallel speedup remain unverified.

Amazon selections 20/24 exposed the first usable string port and `canResume`, yet the model
added two transforms before resuming at 28. The final action list omitted offered `resumeStep`;
that contradiction is confirmed, not its causal effect. Judge 30's business truth remains
unadjudicated. Repair 33 added arguments, but the [original `maxRecords` contract](compact-taskplan-c17ee8a42-amazon-contract-assessment.json) bounds observations **per page**, not
product count; no follow-up Judge accepted it. There was no browser operation or execution.

Root's exact Code inspection separates the missing clone head in draft 16, added by 19, from
Judges 20/22's unsupported demand that the one-reference clone also consume the review base
while its scope remained open. Base provenance remains a downstream review obligation.
Repair 23 changed only wording with unchanged inputs: the non-progress rule applies, but its
antecedent base finding is unsupported. Code's separately frozen review ceiling was 12,000;
no matched improvement, avoided correction or benefit from that ceiling is established.

The **2026-10-24 14:18:30 UTC** audit reports 2,693 physical attempts,
**EUR110.48766248857511267856460945 known + EUR22.730310014607438908749723858 reserved**,
with retained upper bound **EUR133.21797250318255158731433331** against
EUR240.0301038327063045424449473: **EUR106.81213132952375295513061399 available**.
Reservations for 34 unknown physical attempts remain; all 24 receiptless identities are
permanently closed, with zero unresolved identities. No reconciliation releases funds.

Exact c17 publication passed **12/12**, zero compiler diagnostics; its full solution and final
planner CI await terminal evidence. Separately, next-source resume-action and Judge guidance
passed 74 and 140 focused tests; the Code prompt clarification passed 137 existing host tests.
The latter exposes the existing clone-branch restriction and requires fresh scenario input
in both future comparison arms. Amazon's optional review input 12,000 profile is prepared only.
No general convergence or causal improvement is established; source/profile attribution is retained below.

## Retained source `9b3da541c` diagnostic snapshot

**Retained source `9b3da541c` passed one of three business cases; PR #120 stays Draft.** Its
Collections passed both actual runtime variants and the unchanged independent oracle after
31 calls and no corrections. Amazon stopped on a response-less transport failure; its exact
authorized abandonment is applied, with the full reservation retained. Review PR stopped
after 37 calls, three corrections and three checkpoints, without an artifact or execution.
The [source-bound terminal index](compact-taskplan-9b3da541c-live.json) preserves measurements,
review provenance, closure evidence and the latest audit separately.

These single observations do not establish general convergence or a causal improvement.
The 18 alternating runs against `56942f5` remain gated on all three cases executing and
passing independent oracles under one frozen configuration per arm. Earlier failures and
their original source/settings attribution remain immutable below. A later source must
establish its own live acceptance; the retained Collections success is not transferred.

## Retained source `4857d5558` diagnostic snapshot

Source `4857d5558a153f14a26adde9a203ef303609743c` passed both Collections runtime
variants. Amazon and Review PR stopped without artifacts or execution. These separately
frozen profiles establish neither three-case acceptance nor eligibility for the 18-run comparison.

| Frozen case | Calls / physical attempts / receipts | Corrections / checkpoints | Provider input / output tokens (reasoning subset) | Planning seconds | Known EUR / retained reservation EUR |
| --- | ---: | ---: | ---: | ---: | ---: |
| [Collections](compact-taskplan-4857d5558-collections.json) | 33/33/33 | 3/8 | 76,711 / 39,420 (33,252) | 795.509 | 1.3977286925479696564033913433 / 0 |
| [Amazon](compact-taskplan-4857d5558-amazon-v2.json) | 43/47/43 | 4/2 | 209,464 / 65,449 (56,186) | 2,483.914 | 2.6870058009817045961624274880 / 3.7234805890227576974564926372 |
| [Review PR](compact-taskplan-4857d5558-code.json) | 36/37/36 | 1/4 | 152,605 / 34,361 (26,494) | 1,123.287 | 1.6009415439535921463632307006 / 1.0154975457385095939312806783 |

Collections' exact reviewed artifact passed the unchanged `cleanup`, `declared_effects`
and `exact_outputs` obligations in both variants, without effects or violations.
Nominal/alternate runtime was **405.4429 / 2.4024 ms**; different inputs do not establish a
speedup. Useful progress is **33/3 = 11 calls per frozen oracle obligation**, not per generated
task. A pure output alias and finite capacity of 100 remain explicit limitations.

Amazon requests 2 and 3 each recorded HTTP500,500,200; all four failed-attempt reservations
remain. Its 48 constructed identities comprise 43 dispatched, three prepared-only and two
admission rejections: prospective requests needed 19,146 and 19,705 tokens against 18,000.
The [separate cleanup assessment](compact-taskplan-4857d5558-amazon-cleanup-assessment.json)
finds two produced-handle necessity claims unsupported by the retained close contract;
selected promised outputs remain a distinct mismatch. Only public input and an empty
boundary were accepted. No executed cleanup or avoided correction is claimed.

Review PR request19 recorded HTTP500,200 and retained the failed-attempt reservation.
Of 39 constructed identities, 36 dispatched, two remained prepared-only and mandatory
Judge37 was denied before dispatch at **9,399 > 9,000** estimated input tokens. Root's
hash-bound original inspection found a duplicate fetch chosen despite complete accepted-fetch
and clone evidence at selection28; this was **not an explicit suspended-consumer resumption**.
Later Judge34 lacked the prior fetch arguments and correctly identified the candidate's
missing `refSpec`; that local fact does not prove a global omission. The accepted clone also
lacked the pinned branch required by execution authority. No Code artifact or runtime passed.

Exact original response bytes / outside-string whitespace were **21,440/9** for Collections,
**26,358/81** for Amazon and [**24,347/1** for Review PR](compact-taskplan-4857d5558-code-formatting.json);
all completed receipts were nonempty. Per-phase percentiles and reconciled components remain
in the linked archives. Logical-call elapsed includes host/retry work, not verified provider-only
latency. Code money comes from exact retained terminal decimals; its earlier rounded safe
projection remains preserved. The post-Code audited remainder is **EUR110.83671856736400284357327356**,
with 24 permanently closed receiptless requests and zero unresolved requests. Prior exposure
remains separately retained. No matched improvement or avoided-correction claim follows.

## Current behavior and technical evidence

The incremental TaskPlan retains original requirements, deterministic validation,
local/intermediate Judges, checkpoints and JavaScript → YAML v15 compilation. Current-step
discovery, bounded owner/origin facts and exact original contracts preserve scope and
execution authority. Ordinary same-scope results need no exports; boundary crossings do.
Open containers remain unavailable. Inconclusive reports and admission failures do not
create correction or execution authority; no final whole-workflow Judge was added.

Fresh transport permits at most three synchronous attempts after complete durably saved
transient responses. Every physical attempt requires financial, token, call, time and
mandatory-review admission; failed unknown usage remains reserved after successful retry.
Saved success recovers before new-dispatch admission. Historical bytes/policies remain
unchanged. Planning captures five hours separately from runtime limits. Oversized evidence
batches can narrow to a singleton without charging host-only admission as a repair;
indivisible oversized requests stop. Existing business findings retain correction limits.

| Exact source or validated change | Technical result, with warnings as errors |
| --- | --- |
| [Bounded accepted-operation context and rollback presentation](compact-taskplan-accepted-operation-context-validation.json) | **2,878 full Planning / 62 targeted host tests passed**, plus 406 overlapping focused cases; zero failures/skips/compiler diagnostics, 1,986 source files unchanged. Whole accepted-task evidence and original rebuild inputs remain bounded and retrievable; historical requests and authority are preserved. Fresh business acceptance remains to be tested |
| [Retained 9b3da541c](compact-taskplan-9b3da541c-live.json) | **12/12 publication stages passed**, six independent packages, Planning Native AOT presentations 2–5, published encrypted storage and Git AOT/contracts; zero compiler diagnostics. Its exact full solution passed **7,988 / 0 failed / 13 skipped** across 33 projects |
| [Closure identity and retained review support](compact-taskplan-review-support-validation.json) | **2,862 full Planning / 62 targeted host tests passed**, WAE, 1,986 source files unchanged and matching the tested working tree. The original automatic-closure approval and missing cited-evidence defects are reproduced; later exact9b Collections runtime acceptance is recorded separately |
| [c7f7629f publication](compact-taskplan-c7f7629f-collections.json) | **12/12 publication stages passed**, six independent packages, Planning Native AOT presentations 2–5, published encrypted storage and Git AOT/contracts. Its live run still stopped without an artifact |
| [Combined-review approval correction](compact-taskplan-combined-approval-validation.json) | **2,840 full Planning / 62 targeted approval, delegation and persistence tests passed**. The verifier recognizes exact v5 combined coverage; legacy separate-review checks remain. Later runtime acceptance retains its exact9b source attribution |
| [Retained 361bb713](compact-taskplan-9b3da541c-live.json) | **7,950 full-solution passed / 0 failed / 13 skipped**, 33 projects, zero compiler diagnostics; **12/12 publication stages passed**. The later approval correction is outside this source |
| [Revision-5 accepted-state changes](compact-taskplan-accepted-state-validation.json) | **281 focused Planning / 47 host-journal-storage tests passed**; managed smoke passed with exact receipt recovery and v15 execution for presentations 2–5. File identities bind this pre-live result; full solution, Native AOT and business acceptance remain separate |
| `4e38426b` | **7,850 full-solution passed / 0 failed / 13 skipped**, 33 projects, 4,504 unchanged source files; **12/12 publication stages passed**. These results cover the retained source, not the subsequent revision-5 implementation |
| Citation-field guidance over `6308c479` | **59 focused tests passed**, including actual projected requests, unchanged structural grammar, revision-3 bytes and pending revision-4 one/two-citation recovery. This later change is outside the exact630 full/CI results |
| `6308c479` | **7,846 full-solution passed / 0 failed / 13 skipped**, 33 projects, 4,499 unchanged source files; **12/12 publication stages** and all three applicable CI workflows passed. The instruction patch has **90 existing focused tests passed**; those retain their tested `7697449` base attribution |
| `7697449c` | **100 focused / 2,721 full Planning passed**, no failures/skips/compiler diagnostics; committed files match the tested repair patch; **12/12 publication stages passed**. **7,846 full-solution passed / 0 failed / 13 skipped**,33 projects,4,490 unchanged source files. [Planner CI](https://github.com/GnouGo/GnouGo/actions/runs/40038029326) passed; the earlier pending snapshot remains immutable |
| `a9348669` | **7,839 full-solution passed / 0 failed / 13 skipped**, 33 projects; 117 focused/2,714 Planning and 12 publication stages passed. This historical source does not contain repair version 12 |
| `975fd04f` | **7,834 full-solution passed / 1 stale assertion failed / 13 skipped**, 33 projects, no compiler diagnostics. The single-diagnostic assertion was corrected in `a9348669`; 81 historical-fixture focused tests passed with one intentional browser skip |
| `504a557d`, `7e19f7f8` | Each 12 publication stages passed. `504a557d` full solution: 7,811 passed/23 failed/13 skipped; 22 historical-fixture failures and one stale assertion, corrected later |
| `e0959e3` | 12 publication stages passed; full solution 7,796 passed/22 historical-fixture failures/13 skipped; original failures retained |
| `d0413d2` | 7,690 full-solution passed/0 failed/13 skipped; 237 focused/2,661 Planning; 11 publication stages; [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/40002249895) |
| `f3cb064` | 7,685 full-solution passed/0 failed/13 skipped; 92 focused/2,656 Planning; 11 publication stages; [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/39994400563) |
| `35022fa`, `131801d4` | Historical full-solution passes 7,674 and 7,661, each 0 failed/13 skipped; `35022fa` also 11 publication stages and [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/39968367834) |

The [terminal630 technical index](compact-taskplan-6308c479-technical-validation.json) binds
its exact full solution, publication and [planner](https://github.com/GnouGo/GnouGo/actions/runs/40047679529),
[Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/40047679537) and
[standalone](https://github.com/GnouGo/GnouGo/actions/runs/40047679420) CI passes.
Publication covers six independent packages, Planning AOT revisions 2/3/4, published encrypted
storage and Git AOT/contracts. The [earlier769 index](compact-taskplan-7697449-technical-validation.json)
retains its dated pending observations; its [Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/40038029318)
and [standalone](https://github.com/GnouGo/GnouGo/actions/runs/40038029330) passes remain separate.
Historical a934 [standalone rerun](https://github.com/GnouGo/GnouGo/actions/runs/40033155614/attempts/2),
[Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/40033155627) and
[planner](https://github.com/GnouGo/GnouGo/actions/runs/40033155679) passed; its first artifact-service
ETIMEDOUT remains preserved. Technical passes do not establish business acceptance.

Earlier focused evidence remains in the [confirmed-retry](compact-taskplan-confirmed-retries-validation.json),
[evidence-recovery](compact-taskplan-evidence-recovery-validation.json),
[same-scope](compact-taskplan-same-scope-result-validation.json) and
[repair](compact-taskplan-invalid-retained-repair-validation.json) indices; filters overlap.
Fresh repair version 12 excludes JSON(original) only for an exact required TASK_LITERAL_INVALID
at that binding. Valid conversions, ordinary multiline replacements and issued 10/11 requests
retain their original authority and recovery. Technical passes do not establish live convergence.

The [citation-field experiment](compact-taskplan-citation-field-validation.json) moves two existing
sentences from the verified Judge instructions into `evidence.description`, without duplication.
The offered pointer choices and grounding checks remain unchanged. This addresses the missing
field-level guidance observed in Amazon's unsupported report; it does not establish that the
model will cite originals correctly or resolve the separate semantic failures below.

## Earlier completed live diagnostics

| Frozen attempt | Logical calls / physical attempts / receipts | Corrections / checkpoints | Planning seconds | Known cost EUR | Terminal result / runtime |
| --- | ---: | ---: | ---: | ---: | --- |
| [9b3da541c Collections](compact-taskplan-9b3da541c-live.json) | 31/31/31 | 0/9 | 822.106 | 1.5068630075858991521642124051 | Reviewed artifact; both actual runtime variants passed the independent oracle |
| [9b3da541c Amazon](compact-taskplan-9b3da541c-live.json) | 2/2/1 | 0/0 | 343.929 | 0.0509772423025435073627844712 | Response-less Transport; exact abandonment applied, full reservation retained; no artifact/runtime |
| [9b3da541c Review PR](compact-taskplan-9b3da541c-live.json) | 37/37/37 | 3/3 | 887.150 | 1.7608835341365461847389558231 | Invalidation/rebuild stopped on preserved-literal and non-progress guards; no artifact/runtime |
| [c7f7629f Collections](compact-taskplan-c7f7629f-collections.json) | 31/31/31 | 2/7 | 861.788 | 1.4424230254350736278447121815 | False finite-capacity findings; post-repair Judge exhausted all 8,192 output tokens on reasoning; no artifact |
| [361bb713 Collections](compact-taskplan-361bb713-collections.json) | 41/41/41 | 3/11 | 910.990 | 1.4186836233824185631414547077 | Static artifact oracle passed; approval rejected combined review coverage; no execution |
| [4e38426b Collections citation](compact-taskplan-4e38426b-collections.json) | 11/12/11 | 0/2 | 508.198 | 0.2951584114234716644355198573 | All-null Judge action; full failed-attempt reservation retained |
| [6308c479 Collections scope2](compact-taskplan-6308c479-collections-scope2.json) | 27/28/27 | 3/5 | 860.385 | 0.8997188755020080321285140564 | Premature public-result requirement closure, then identical rejected replacement |
| [6308c479 Collections](compact-taskplan-6308c479-collections-pending.json) | 2/3/1 | 0/0 | 635.877 | 0.0153904506916555109326193664 | HTTP500 followed by response-less Transport; exact permanent closure now applied |
| [7697449 Amazon review9](compact-taskplan-7697449-amazon-review9.json) | 31/31/31 | 3/0 | 810.637 | 1.5096340919232485497545738509 | Judge 31 omitted original-obligation grounding; report rejected |
| [7697449 Collections review8](compact-taskplan-7697449-collections-review8.json) | 47/47/47 | 4/8 | 1,075.530 | 1.8781838464970995091477019188 | Grounded redundant final alias; no empty Judge response |
| [7697449 Collections](compact-taskplan-7697449-collections.json) | 17/18/17 | 1/3 | 714.674 | 0.6770771976796073181615350287 | Intermediate Judge 17 exhausted 4,096 reasoning tokens, zero text |
| [7697449 Amazon](compact-taskplan-7697449-amazon.json) | 40/40/40 | 2/4 | 1,000.806 | 2.0205310129406514948683623382 | Mandatory Judge 41 denied before dispatch: 8,555 > 8,000 input tokens |
| [7697449 Review PR](compact-taskplan-7697449-code.json) | 25/25/25 | 3/1 | 856.946 | 1.6618652387327086122266845156 | Grounded missing caller review text persists after three repairs |
| [a9348669 Review PR](compact-taskplan-a934866-code.json) | 51/52/50 | 2/5 | 1,657.740 | 2.7922579205711735832217759928 | Confirmed HTTP 400: repair schema retained an already invalid literal |
| [975fd04f Review PR](compact-taskplan-975fd04-code.json) | 39/39/39 | 4/3 | 959.263 | 1.9388041053101294065149486836 | False same-scope availability premise, export repair, then zero-action Judge |
| [504a557d Amazon](compact-taskplan-504a557-amazon.json) | 13/13/13 | 0/1 | 417.994 | 0.8039446675591253904506916556 | Judge omitted original-obligation grounding; handle allegation unproven |

Collections `9b3da541c` passed the three frozen oracle obligations `cleanup`,
`declared_effects` and `exact_outputs` with one reusable parallel
doubling path, ordered results and duplicate preservation. Actual nominal/alternate runtime
was **356.3307 / 2.3367 ms**; these different inputs do not establish a speedup. Its 31 original
receipt texts contain 18,062 UTF-8 bytes and 11 whitespace bytes outside JSON strings.
Amazon request2 remains receiptless after exact authorized closure: all original evidence and
**EUR 0.9308701472556894243641231593** remain retained, and replay/continuation are forbidden.
The historical pending archive is unchanged; closure does not prove provider termination.

Inspection of Review PR's retained originals found two accepted clone tasks with the same
operation and arguments; each local Judge returned no findings. Judge22 showed the earlier
task ID in navigation, but neither its arguments nor exact task evidence, and had no consumed
imports. Later invalidation required
rebuilding accepted work, and changing an original literal was correctly rejected. Neither
clone executed. The 41 measured request identities comprise 37 dispatched requests and four
prepared-only drafts, not four recorded admission rejections. Its 37 original responses contain
24,576 UTF-8 bytes, two whitespace bytes outside strings and no empty response; the separately
normalized representation is 25,277 bytes. The [formatting supplement](compact-taskplan-9b3da541c-code-formatting.json)
preserves that distinction. These measurements do not explain the semantic failure or prove
an avoided correction. Full per-phase/component measurements and original hashes remain
linked from the compact index rather than duplicated here.

Collections `c7f7629f` returned 31 known receipts with no new unknown reservation. Judge29
cited the original clauses and loop, but inferred unlimited required capacity from unspecified
input length. The existing finite-guard guidance was conditional on a selected `foreach`;
this later value/closure review did not receive it. Repair30 withdrew root closure and plan
completion while retaining requirement closure claims. Judge31 then returned HTTP200/length,
8,192 reasoning tokens and zero text. These observations do not establish the cause of
output exhaustion, defect resolution or avoided corrections.
Retained visible responses total 18,200 UTF-8 bytes with only 14 bytes of JSON whitespace
outside strings; none was whitespace-heavy by this measurement. The post-run campaign audit
shows **EUR125.51096667088921480252017225 available**, retaining all 23 permanently closed
receiptless identities and 28 unknown attempts. This is a dated balance before later calls.

Collections `361bb713` retained five checkpoints with explicit combined local/intermediate
coverage, but artifact approval still looked for separate purpose records. The original
verifier correctly stopped execution under its existing contract; the integration mismatch
requires a focused verifier correction and a fresh diagnostic. No receipt was fabricated or
review authority widened. All 13 Judge responses were nonempty and contained no findings;
there was no report correction or reassessment. Generation peaked at 6,343 estimated input
tokens and Judges at 5,890, with complete-request component bytes reconciled. The first
loop's output was visible during selection, but the model built a second identical producer
whose result alone became public. Missing type detail is a presentation hypothesis, not a
demonstrated cause. The static oracle and 11 checkpoints do not establish business success.

The post-run audit retained all 23 permanently closed receiptless identities and 28 unknown
physical attempts, with **EUR 126.95338969632428843036488443 available**. This is a dated
balance before later dispatches. Amazon on this source was frozen but not dispatched once
the approval mismatch was identified. Exact metrics, evidence hashes and accounting remain
in the [361 archive](compact-taskplan-361bb713-collections.json).

The [original630 pending snapshot](compact-taskplan-6308c479-collections-pending.json) remains
unchanged. Its [separate authorized closure](compact-taskplan-6308c479-collections-retained.json)
applied at 2026-10-24 05:47:25 UTC, binding evidence `3f76285f…230c406` and retaining the full
**EUR 1.8617402945113788487282463186** request reservation. The first attempt returned HTTP500;
the second recorded Transport without a response. This is not a verified timeout or empty completion.
Captured zero uncertain retries stopped further sends. No Judge, checkpoint or artifact was reached;
replay and continuation of that exact run are permanently forbidden. Scope2 used a fresh identity
with unchanged source/settings; this closure supplied no runtime authority.

Scope2 accepted a reusable multiplication group and per-item call within a parallel foreach,
but no public root result. Judge17 incorrectly treated finite maxItems100 as a violation despite
explicit guidance that unspecified input length does not require unlimited capacity; repair18
raised it to1000. Judge26 correctly rejected closing the public-result requirement while only
exporting a scalar from the nested body. Repair27 offered withdrawal via `reselectStep`, but
repeated draft25 byte-for-byte, so the non-progress stop was justified. These are distinct findings,
not avoided corrections. All27 responses were nonempty and every request fit its captured limit;
generation peaked at4,762 estimated input tokens and local Judges at3,971. HTTP500→200 at
request2 retained EUR0.9308701472556894243641231593 without a new receiptless identity.

Collections' local Judge 16 returned no findings; intermediate 17 consumed its retained
high reasoning 4,096 output allowance. Its complete request was 9,379 bytes/3,383 estimated input
tokens, below 8k. A closed multiplication group exists, but the parallel loop opening remains
tentative: no ordered duplicate-preserving reusable calls or public result were executed.
The [phase supplement](compact-taskplan-7697449-collections-phase-corrections.json) attributes
one genuine correction to an invalid group-input binding. HTTP 500→200 recovered during
extraction, retaining **EUR 0.9308701472556894243641231593**; no new receiptless request exists.

Collections review8 closed its reusable multiplication group and parallel loop, exported the
root result and closed both model requirements; the root boundary still remained open.
Twenty Judge receipts were nonempty; two used 6,437 and 4,583 output tokens, above the old
4,096 allowance. This different generated trajectory does not prove a matched-request cure.
[Judge 38](compact-taskplan-7697449-collections-review8-judge38.json) incorrectly attributed
new output work to an empty root-closure candidate; its selected objective nevertheless
remained mismatched to the caller-declaration requirement. Final selection legally offered
root closure, but the model chose another value alias. Judge 47 correctly identified that
actual redundancy after four corrections. Closure authority was present; neither static
structure nor model requirement closure proves execution. Separately, the
[reviewed wording patch](compact-taskplan-scope-basis-wording-validation.json) corrects an instruction overclaim: `scopeInterface` includes candidate additions
only with `basis:tentative_candidate`; `draft.outputs` lists additions. Its 90 existing focused
tests passed WAE; the patch is included in technically validated `6308c479`, while its first
diagnostic stopped before any Judge. Scope2 followed a different trajectory and did not reach
the same root-closure review. No matched effect on Judge38 or
convergence is established; schemas, grounding and authority are unchanged.

Amazon's 45 constructed requests comprise 40 receipts, 4 prepared and one denied review.
Judge 41's 24,895 bytes include 10,286 bytes of the necessary whole observation interface;
no obsolete catalog or safe normative pruning was demonstrated. A separately frozen 9k
ceiling could admit it, without proving semantics or convergence. Sixteen discovery calls
include repeated original browser pages across steps. The first executable producer was
accepted after call 18; no requirement closed. Repair 17 changed wording after a handle/output
claim mismatch; handle necessity and avoided correction remain unproven. The other repair
selected a missing URL producer. Full phase/formatting data stays in the immutable archive.

Amazon review9 repeatedly selected a close step for a new session-producing objective.
Reselections 8/14 and repair 21 account for its three corrections; 20 mixed an extension with
reselection and was correctly rejected. Sole Judge 31 cited a concrete task plus step/operation,
but no offered original requirement. This unsupported report correctly stopped without a
checkpoint or reassessment; it was not an explicit inconclusive response. Its partial operation
description limits assessment of the handle claim. All requests fit their ceilings; Judge31
needed only 3,854 estimated input tokens, so the 9k allowance provided no demonstrated benefit.

Review PR accepted a clone with the exact repository URL, pinned head and fixed directory.
Its next action omitted `input(reviewText)` despite a typed available declaration and legal
JSON object/array binding grammar in every author request. Repairs 20/22/23 did not supply it;
22 also duplicated the `task` input and was correctly rejected. Judge 24 retrieved the exact
original declaration, then 25 grounded the omission. No same-scope/export or premature-closure
finding recurred. All Judges were at most 6,820 input tokens; the 9k ceiling's benefit is unproven.
The frozen toolchain PATH was verified, but no checks, report or cleanup executed. Optional
provider latency checkpoints are not independently measured HTTP durations. None of these
runs demonstrates avoided corrections or causal convergence improvement.

The a934 archive separately retains 500→200 and confirmed schema 400 reservations totaling
EUR 1.5331593038821954484605087015; the existing rejection operator created no receipt/refund.
Its clone still uses a PR-page URL and lacks required pinned provenance; unchanged execution
guards would reject it. Six earlier Judges without findings do not establish an avoided repair.
Prior [7e19 Review PR](compact-taskplan-7e19f7f-code.json), [Amazon](compact-taskplan-7e19f7f-amazon.json)
and [formatting](compact-taskplan-7e19f7f-response-format.json), [e095 Collections](compact-taskplan-e0959e3-collections.json),
[1b Amazon](compact-taskplan-1b749a1-amazon.json), [d041 Review PR](compact-taskplan-d0413d2-code.json)
and [Amazon](compact-taskplan-d0413d2-amazon.json) retain their distinct failures; the latter
reached the former host 30-minute limit.
Earlier controls remain [f3 Amazon](compact-taskplan-f3cb064-amazon.json),
[Review PR](compact-taskplan-f3cb064-code.json) and [high-navigation Amazon](compact-taskplan-f3cb064-amazon-high.json).
Source/settings differences and unmatched runs cannot establish architectural causality.

## Audited allowance after 9b Review PR

The 2026-10-24 10:59:16 UTC [audit](compact-taskplan-9b3da541c-live.json) has **24 receiptless
identities, all permanently closed**, no unresolved request and **29 unknown physical-attempt
records**. Successful logical requests can retain failed-attempt reservations; those counts
are distinct. Closing Amazon's exact request retained its full reservation.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 100.77739921325159639119290038 |
| Retained unknown-usage reservations | 17.991331879846171617361950542 |
| Spending/reservation upper bound | 118.76873109309776800855485092 |
| Available after 9b Review PR | 121.26137273960853653389009638 |

The earlier [scope2 archive](compact-taskplan-6308c479-collections-scope2.json),
the [retention follow-up](compact-taskplan-6308c479-collections-retained.json) and
earlier [Amazon review9 archive](compact-taskplan-7697449-amazon-review9.json) retain their own
dated balances. With prior lost historical exposure EUR 159.96989616729369545755505266,
independent cumulative spending/reservations are **EUR 278.73862726039146346610990358**
within the original EUR 400 authorization. Original CLR decimal precision differences remain
disclosed in the index. No later spending is inferred from this audit.

## Acceptance and chronological immutable evidence

Earlier `efcb1d7a` and retained `9b3da541c` passed both Collections runtime variants. Amazon
and Review PR still require complete execution and independent oracle acceptance before
paired runs. Artifact review and exact individual confirmations remain mandatory. Collections
has three accepted frozen oracle obligations and 31/3 calls per obligation, not per produced
task or extracted user requirement. Amazon and Review PR
have zero, so their ratios remain undefined. Compilation, checkpoints and technical passes
are not business acceptance.
No avoided correction, causal convergence improvement or eventual success is claimed.
Archives below are grouped newest to oldest; their failures and validation scope are immutable.

- Exact9b [three-case index and audit](compact-taskplan-9b3da541c-live.json) and [Code formatting supplement](compact-taskplan-9b3da541c-code-formatting.json) link the immutable measurements, actual Collections execution, historical Amazon pending snapshot and separate applied closure.
- Prior diagnostics: [35022fa Collections](compact-taskplan-35022fa-collections-pending.json) and [authorized closure](compact-taskplan-35022fa-collections-retention.json); [2d6cbce Amazon](compact-taskplan-2d6cbce-amazon.json) and [Review PR](compact-taskplan-2d6cbce-code.json). Their failures and validation scope remain immutable.
- Frozen `131801d4`: [Collections control](compact-taskplan-131801d4-collections.json), [Amazon](compact-taskplan-131801d4-amazon.json), [Review PR](compact-taskplan-131801d4-code.json), [Collections output32](compact-taskplan-131801d4-collections-output32.json), [Collections high Judge](compact-taskplan-131801d4-collections-high-pending.json) and [its authorized abandonment](compact-taskplan-131801d4-collections-high-retention.json). All five failed to reach artifact execution; output-limit, correction and transport evidence remains separate. No causal reasoning-profile benefit or avoided corrections is proven.
- Prior producer-proof failure: [b1c29b359 Collections](compact-taskplan-b1c29b359.json).
- [615dd0e96 Review PR](compact-taskplan-615dd0e96.json) and [exact authorized closure](compact-taskplan-615dd0e96-retention.json), retained separately.
- Previous: [f05d07c2a Amazon](compact-taskplan-f05d07c2a.json), [16d8e83e3 Collections](compact-taskplan-16d8e83e3.json).
- Earlier: [1a cohort a](compact-taskplan-1a236234.json), [b](compact-taskplan-1a236234-b.json), [c](compact-taskplan-1a236234-c.json).
- Earlier: [9d23d5b](compact-taskplan-9d23d5b.json); prior [ae1d50aa](compact-taskplan-ae1d50aa.json), [365c8326](compact-taskplan-365c8326.json), [9246376](compact-taskplan-9246376.json), [2d0485d](compact-taskplan-2d0485d.json); [offline extraction experiments](compact-interface-ce9b610.json) made no paid calls or causal claim.
- [5678374](compact-taskplan-5678374.json), [875022e](compact-taskplan-875022e.json), [61d9f0f](compact-taskplan-61d9f0f.json), [2e5b3a0](compact-taskplan-2e5b3a0.json), [0b44557](compact-taskplan-0b44557.json), [52ebb72](compact-taskplan-52ebb72.json), [d8f51c5](compact-taskplan-d8f51c5.json).
- [92d8813](compact-taskplan-92d8813.json), [407243a](compact-taskplan-407243a.json), [bbd900d9](compact-taskplan-bbd900d9.json), [bb5b726](compact-taskplan-bb5b726.json), [efcb](compact-taskplan-efcb1d7a.json), [d488](compact-taskplan-d488720a.json), [29ffd](compact-taskplan-29ffd813.json).
- [3adc](compact-taskplan-3adc335.json), [4a43](compact-taskplan-4a43ea1.json), [8952](compact-taskplan-8952a0a.json), [743](compact-taskplan-743e4a3.json), [410](compact-taskplan-41077d0.json), [663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json), [afc](compact-taskplan-afc342c.json), [5c](compact-taskplan-5c01f98.json), [08ad](compact-taskplan-08ad5db.json), [cd174ea](compact-taskplan-cd174ea.json).
- [October 19](compact-taskplan-live-2026-10-19.md), [October 20](compact-taskplan-live-2026-10-20.md), [historical components](compact-taskplan-request-components-2026-10-20.json); historical passing CI [52ebb72](https://github.com/GnouGo/GnouGo/actions/runs/39855405218), [0b44557](https://github.com/GnouGo/GnouGo/actions/runs/39857068574).

Archives retain failures, uncertainty and exact validation scope. Original payloads remain
encrypted; safe reports grant no execution authority. Exact630 technical validation is complete;
later-source validation and live results must retain their separate source and settings attribution.
