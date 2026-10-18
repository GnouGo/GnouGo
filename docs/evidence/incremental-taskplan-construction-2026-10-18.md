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

Focused validation is green: 25 incremental construction tests, 94 compiler/v15
and scale tests, 164 Core semantic/expression/budget tests, 185 benchmark/host
safety tests, and 45 frozen-settings tests. The enabled Playwright smoke covers
desktop/mobile clarification, mandatory local review and saved-receipt recovery.
The frontend production build and planning-skill validator pass.

Offline local and collection scenarios both satisfy their independent execution
oracles with no safety violations. These use deterministic fixtures and do not
establish live-model business reliability. The final solution, packaging and CI
results remain in progress.

## Campaign accounting before new dispatch

The existing encrypted `schema-portability-20261002` ledger was inspected through
its public benchmark API. No new paid dispatch had occurred at this inspection.

| Retained measure | EUR / count |
| --- | ---: |
| Existing ceiling | €200 |
| Known cost | €138.73008382586045 |
| Reserved uncertain cost | €21.239812341433254 |
| Conservative upper bound | €159.96989616729368 |
| Unknown physical attempts | 16 |
| Reservations / completion receipts | 1,130 / 1,124 |
| Retained inconclusive request identities | 6 |
| Remaining after the authorized €400 extension | €240.03010383270632 |

The six missing completion identities match the six retained inconclusive
closures; their possible usage remains reserved. The inspection evidence hash is
`e2aa0e4c078eb52eb18aa05b8f5097e262280723adebea049ae6c6a8386d044a`.
This supersedes the earlier €157.463077 snapshot for admission calculations.
The append-only ceiling extension and new cohort outcomes remain pending the
frozen implementation and validation gates.
