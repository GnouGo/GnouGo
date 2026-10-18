# Incremental TaskPlan construction validation

The implementation builds one accepted semantic TaskPlan through bounded drafts,
compiler preflight and local reviews. This report separates construction,
compilation, execution and independent business evidence. Historical campaign
results do not establish reliability for the new construction path.

## Deterministic evidence

Focused coverage includes one-draft completion, choices and clarification,
partial branch/loop construction, typed repairs, invalid evidence, review budget
admission, immutable accepted work, checkpoint recovery and targeted rollback.
The scale fixture constructed 500 operation tasks in 63 accepted drafts. Complete
serialized requests peaked at 10,583 conservative input tokens for generation and
8,004 for Judges. The evidence-review and typed-repair fixtures peaked at 3,151
and 4,485 respectively. Repair with 500 original requirements and 500 retained
answer batches peaked at 4,691. These are complete-request admission estimates
(serialized UTF-8 bytes divided by three, rounded up, plus 256), not provider
token usage.

A separate 500-operation dependency graph executes against an independent exact
input/output oracle. Large policy capacity does not enlarge execution limits.
The retained 500-pure-value fixture produces a 98,268-character fused v15 program,
exceeding the existing 65,536-character execution ceiling. It now fails during
deterministic compilation admission. v15 lowering and its output are unchanged;
the large preset does not guarantee that every graph fits every runtime limit.

A separate 101-sibling conditional fixture initially exposed excessive snapshot
expansion in the complete runtime semantic validator. Validation grew to 45 GB
and was terminated. Removing unused schema materialization exposed a second
failure: recursive structural string keys overflowed on the shared type graph.
Removing those unused copies and comparing types structurally with per-call
memoization fixes both failures. The unchanged v15 output now passes complete
semantic admission in 35 seconds, while the ordinary 100-workflow policy still
rejects it. This fixture validates admission; it does not execute the 101 sibling
conditionals. The separate 500-operation graph supplies the large execution
oracle. Runtime limits and lowering remain unchanged.

The comparer also fixes ambiguous delimiter-based keys, such as one enum value
`a|b` being treated as the two values `a` and `b`. Focused tests cover ordering,
optional fields, shared subgraphs, mutable inputs, cycles and exact equality.

Focused validation is green: 27 incremental construction tests, 94 compiler/v15
and scale tests, 164 Core semantic/expression/budget tests, 185 benchmark/host
safety tests, and 45 frozen-settings tests. The enabled Playwright smoke covers
desktop/mobile clarification, mandatory local review and saved-receipt recovery.
The frontend production build and planning-skill validator pass.

Offline local and collection scenarios both satisfy their independent execution
oracles with no safety violations. These use deterministic fixtures and do not
establish live-model business reliability. All five Flow Release packages build
with warnings as errors. The final-source `osx-arm64` Native AOT planning smoke
and trimmed Agent.Server encrypted-storage smoke pass, including tenant isolation
and pending candidate/review persistence. The complete Integrations suite passes
108 tests, including the definition-only interruption boundary.

The complete Planning suite passes 1,788 tests with warnings as errors. The
first PR CI run retained 29 failures caused by historical fixtures inheriting
newer captured profiles. Those fixtures now explicitly restore their original
profiles; frozen approval and repair-authority hashes and assertions remain
unchanged. Fresh-session compatibility fixtures exercise an incremental draft
and its mandatory local review. Those compatibility corrections change tests only.

The full Server run also exposed a retained workbook fixture that still created
a fresh session. Its eight variants pass after restoring the retained protocol.
Auditing the remaining fixtures added fresh chat recovery and four-task workbook
coverage through incremental drafts and local review. A fresh host commitment
case then exposed a correction-routing defect: an output-only typed patch could
not add a missing required producer. Eligible tentative candidates now use bounded
replacement while preserving issued requests, explicit edit authority and accepted
fragments. The eight host correction/recovery variants pass with exact receipt and
cumulative-accounting assertions; no operation executes during planning.
Together these focused reruns cover all 34 failures retained by the first
complete Server run; its other 1,147 cases passed and one browser smoke was
disabled by default (the separately enabled smoke passed).

The Draft PR records the complete `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`
run and CI conclusion for its final pushed revision. Existing publish-local
warning exclusions remain unchanged; no new blanket suppression was introduced.

## Campaign accounting before new dispatch

The existing encrypted `schema-portability-20261002` ledger was inspected through
its public benchmark API. No new paid dispatch had occurred at this inspection.

| Retained measure | EUR / count |
| --- | ---: |
| Existing ceiling | €200 |
| Known cost | €138.730084 |
| Reserved uncertain cost | €21.239812 |
| Conservative upper bound | €159.969896 |
| Unknown physical attempts | 16 |
| Reservations / completion receipts | 1,130 / 1,124 |
| Retained inconclusive request identities | 6 |
| Remaining after the authorized €400 extension | €240.030104 |

Displayed monetary values are rounded to six decimal places. Admission retains
the exact encrypted decimal amounts.

The six missing completion identities match the six retained inconclusive
closures; their possible usage remains reserved. The inspection evidence hash is
`e2aa0e4c078eb52eb18aa05b8f5097e262280723adebea049ae6c6a8386d044a`.
This supersedes the earlier €157.463077 snapshot for admission calculations.
At `2026-10-18T21:35:12.3800510Z`, the existing append-only operation raised the
ceiling from €200 to €400 with the user's authorization. Its fourth history entry
retains the complete pre-extension accounting. A second inspection confirmed
unchanged reservations, receipts, uncertain identities, failures and accounting.
Only the authorization record changed; the post-extension evidence hash is
`e51baaa3ddb030864ba7f6945f9c5e5582fb7b1a88717a848da5d44b9df8e9af`.

## Live evaluation readiness

No new paid requests or external workflow executions have been dispatched.
The readiness snapshot used source `865a4e7`; no live cohort was dispatched.
New live business outcomes, Judge findings and convergence remain unverified.
New campaign calls, tokens and cost are zero. A future live cohort must freeze
its own final source revision and settings before dispatch.

The exact campaign deployment `gpt-5.5-2026-04-24` has no verifiable current local
deployment limits. The original Agent configuration database is absent and the
readiness check cannot recover its three prior metadata overrides from retained
public manifests. No deployment alias was inferred, configuration fabricated,
historical request replayed or model substituted. The original configuration or
a verified declaration for that exact deployment is required before paid
dispatch. The retained failures and all uncertain usage remain intact.
