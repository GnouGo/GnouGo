# Frozen LOCAL validation: rerun 4

## Outcome

**VERIFIED BLOCKER — category C: a schema-valid, semantically invalid dependency.**
LOCAL started exactly once under `schema5-realized-governing-diagnostics-rerun-4`.
Currency conversion and model transport worked. Admission stopped before committing
canonical operations. MIXED and Stage 1 were not run.

Production remained frozen at
`2f8b177ec6076cfdedbf524bd72a01d3f1b457d7`.

## First meaningful blocker

`INTENT_OPERATION_UNRESOLVED` in `intent_operations`, at
`/operations/@operation_1d213d8df89c1c565c28773f`:
**“Grounded effects contain a dependency cycle.”**

The first invalid assignment was the completed realization decision
`effect_runtime_c15176b2d6fc60156db65bf0` in request 11. It selected the same issued
operation in both `effects` and `producers`:

```json
{
  "effects": ["operation_1d213d8df89c1c565c28773f"],
  "producers": ["operation_1d213d8df89c1c565c28773f"]
}
```

Request 12's governing decision,
`effect_governing_runtime_b671bcde1e1d4b111c68e8b5`, repeated that self-dependency.
Both answers passed their original response schemas. Complete dependency validation
then rejected the cycle. No request failed at the provider or lacked a receipt.

## Root cause

The response domains constrain workflow scope and governing targets, but producer
selection remains independent of the selected effect. Consequently an operation
can select itself as its producer. This is an impossible choice the engine can
identify from canonical IDs; it is not an unresolved business decision.

The realization selected one `main` local invocation consuming canonical `record`
and `threshold` and producing `classifiedResult`. The descriptive contribution was
deferred during realization and later selected that same established effect. Its
governing target domain contained only that realized identity, so the previous
realization-ordering correction held in this run.

The selected boundary was an invocation, not the alternative result-slot anchor.
No standalone occurrence-identity request was needed. These are retained mapping
facts, not a claim that canonical admission or the full classifier passed.

## Authority analysis

The dependency validator correctly prevented a cyclic operation set from acquiring
authority. The remaining gap is in the issued producer domain, which exposes the
selected consumer as a potential predecessor. Governing applicability and producer
dataflow must remain distinct proof concepts.

Interpretation had no unresolved runtime scopes or external/lifecycle/human
evidence. The nine policy runtime scopes remained engine-owned. The supplied
declaration fixture validated exactly required `record`, optional `threshold` with
omission default `100`, and required `classifiedResult` with its enum/preservation
attachments. Preservation remained contract evidence.

All-low reasoning, token ceilings, repair allowances, sixteen reservations and
global budgets were unchanged. No production source, DLL, proof version or fixture
assertion changed.

## Proposed action

**No production patch or replacement diagnostic.** Review effect-to-producer
eligibility before proposing a correction. Apply the protocol's architecture-review
threshold: impossible combinations have recurred across scope, realization and
producer domains. Do not weaken cycle validation, add a prompt workaround or
increase model limits.

## Validation performed

**Offline:** 3,473 tests passed across 29 solution projects, including 975 planner
tests; one existing optional provider test was skipped. All 53 focused harness
tests passed. Five added harness cases cover valid, absent, incompatible and future
quotes, plus redacted transport failure without retry. Existing single-start,
LOCAL-only, receipt and archive protections remain covered.

Harness/test builds were warning-free and did not rebuild production references.
Both frontends, four packages, synthetic admission selfchecks, six classifier/batch
and 18 CodeReview reference cases passed. Native AOT planning/encrypted-restart
and trimmed Agent.Server persistence smokes passed using published artifacts whose
hashes matched retained evidence. Synthetic fixtures are offline evidence only.

**Currency preflight:** the existing .NET ECB integration returned a valid USD →
EUR quote dated September 15, checked September 16 at 06:16 UTC. This check made no
model call and wrote no planning state. The quote was not injected into LOCAL;
the actual budget used an independently created exchange-rate provider. The
preflight result was recorded in the campaign manifest.

**Historical replay:** the prior stopped case remained unchanged. Read-only audit
checked all twelve new receipts against their original schemas, finding no schema
violations. Reuse of completed decision pages reproduced the same dependency-cycle
diagnostic without provider calls or persisted mutation. No synthetic answer
replaced a live response.

**Live:** one fresh LOCAL, twelve reservations and twelve verified responses. Ten
interpretation pages completed, followed by one realization page and one governing
page. No repair, partition, escalation or retry occurred. Frozen production hashes
and all archived accounting matched after the run.

## Decision accounting

| Work | Calls | Model decisions | Input | Output | Reasoning, included in output |
|---|---:|---:|---:|---:|---:|
| Interpretation | 10 | 22 | 23,335 | 2,755 | 1,331 |
| Realization grounding | 1 | 2 | 2,548 | 808 | 484 |
| Governing grounding | 1 | 1 | 1,063 | 270 | 89 |
| Occurrence identity | 0 | 0 | 0 | 0 | 0 |
| Total | 12 | 25 | 26,946 | 3,833 | 1,904 |

Final-answer tokens, derived from compatible usage counters: **1,929**. Largest
estimated input: **4,578**; largest actual input: **2,830**. All usage is available.
Semantic repairs by both phase/gate groups were zero; partitions and escalations
were zero. No additional planner decisions were introduced or removed by this
harness change.

The retained responses selected one operation identity and one governing mapping.
Admission stopped before committing proofs and resolution events, so uninstrumented
staged deterministic-assignment and attachment counts remain **unknown**. No
canonical operation set was committed; executable holes and construction were not
reached. Do not interpret the harness's empty committed counters as absence of
staged work.

## Next gate

**Architecture review.** Stop here. No production fix, replacement LOCAL, MIXED,
behavior acceptance or Stage 1 was started.

The [redacted report](planner-realized-governing-rerun-4.json) retains exact decision,
request/receipt and checkpoint identities, reference-only assignments, issued
effect domains, accounting, preflight evidence and archive checks. Private requests
and full responses remain in encrypted storage.
