# Exact task presentation sharing

The frozen Amazon run at `39d567bb` reached its first useful planning checkpoint
after nine calls, 377.20 seconds from run start (360.21 from first HTTP dispatch),
and EUR 2.5373578145991939095387371249 captured-tariff cost. It later repeatedly
reselected a source consumer, claiming `foreach.maxItems` was absent. The retained
actual HTTP schemas at calls 15, 17, 20 and 23 all contain that required field,
bounded to 1..10,000. The producer's 20,000-item bound was model-authored; the
original business request did not authorize a larger control capacity.

The run was cancelled and permanently retained without execution or released
reservation. Its [phase counts, milestones and accounting](../pr120-amazon-unclassified-context-2026-11-11/stopped-run.json)
remain distinct from a global review, execution and independent oracle.

Two fresh, independent [negative controls](negative-controls.json) reused the
exact call-15 fixture: medium reasoning alone, then an unchanged low-reasoning
request with a small guide derived from the actual schema. Both completed with
native protocol/usage and durable receipts, but both repeated the unsupported
missing-field claim and returned only reselection. No candidate was applied.
These trials justify neither a reasoning change nor production field-guide
instructions. Their harness-level schema passes are not useful-fragment passes.

## Demonstrated duplication and generic correction

Code24 call 42 was already at low reasoning with a 32,768-token allocation. Its
61,557-byte prompt includes complete scope evidence and imported producer task
views that repeat the exact same declarations. Existing fresh preparation alone
changes that request's size negligibly; that is not a meaningful new paid test.

The existing production prompt-sharing path now replaces only those repeated
views with `taskPresentedAt` pointers to an existing declaration in the same
context. Matching requires the same task ID, scope, `tasks`/`always` phase and
complete declaration bytes. All original arguments, outputs, control bodies and
cleanup stay in scope evidence; imported values remain distinct. Different
arguments, identities or scopes do not share. No second plan/cache, contract,
binding, repair slot, permission or execution authority is introduced.

The existing short-address mapper rewrites only the pointer's evidence-key
segment in either direction. Literal TaskValue strings and original schemas stay
exact. Request identity is bound after presentation as before; old requests,
evidence maps, counters, receipts and stopped sessions remain immutable.

[Offline application to the exact Code24 fixture](code24-presentation.json)
reduces the prompt from **61,557 to 54,805 UTF-8 bytes**, with five resolving
task links and unchanged strict schema, low reasoning and output allocation.
This establishes a reduction, not a transport root cause or latency improvement.

Two regressions first failed before implementation. All **2,978 planning tests**
then pass in Release with `-warnaserror`, no skips, including execution/cleanup
phase separation, changed-argument/scope/ID rejection, exact literal preservation,
pointer escaping, issued-map validation, canonical recovery and cold restoration.
No model dispatch or accepted checkpoint occurs in these controlled checks.

## Independent representative Code delivery

On frozen `8f2d02f4`, [code24-task-sharing-20261111-01](code24-live-probe.json)
uses a new independently admitted identity, the exact original baseline and schema,
low reasoning and 32,768 output tokens. Only the task-sharing presentation changes.
Its single real HTTP attempt completes in **66.733423 seconds**, with first nonempty
assistant output after **65.187167 seconds**, complete native protocol termination
and known usage: 18,343 input, 1,376 output and 186 reasoning tokens. Captured-tariff
cost is **EUR 0.7147066726377071204657411554**. Its durable receipt is reloaded.

The returned report/save fragment passes production continuation and complete
technical validation with zero findings on a clone of the exact issued baseline.
No fragment is applied, plan accepted or effect executed; the stopped Code24
session and its old uncertain request remain unchanged. This validates representative
delivery and a useful technical contribution, not the proxy's precise failure layer
or full Code success. Global review, real execution, persisted command evidence,
cleanup and the independent oracle remain required. SDK05 remains the positive
provider/SDK reference; no reconfiguration is made.
