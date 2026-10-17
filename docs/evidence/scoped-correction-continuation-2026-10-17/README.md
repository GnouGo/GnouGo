# Exact correction scopes and bounded continuation

Baseline: `64be0c0`. Runtime/compiler profile remains `compact-bindings-v15`.
Implementation commits: `8b45e85` (output routing), `313e076` (initial continuation).
No provider call, live execution, financial change, historical revision or approval.

## What changed

| Reproduction | Previous behavior | Fresh private correction version 5 |
| --- | --- | --- |
| Accepted nonnullable output bound to a nullable producer port | `/outputs/name` did not identify its editable value; correction escalated to a whole proposal | Resolve the exact accepted name to `/root/outputs/name`, then issue one value slot |
| Initial regeneration fixes requirement metadata but exposes an existing binding defect | Stop after regeneration despite one remaining correction | Freeze the newly accepted candidate; permit one input/output value patch within the remaining allowance |
| Patch changes independent status, repeats the failure, or has uncertain completion | Must stop safely | Still stops; no new regeneration, counter reset, automatic approval or replay |

Only literal, unambiguous accepted output names are routed. Punctuation, slashes and
Unicode remain valid names. Missing operations, guards and producer declarations do
not gain follow-up edit authority. Historical version-4 bookkeeping and already-issued
requests retain their original behavior.

## Retained live evidence

The two stopped `commitmentfix20261017a` records were read through public encrypted
storage APIs. Their record hashes match before and after inspection. See
[retained-replay.json](retained-replay.json).

* **Amazon:** the saved lookup takes two objects instead of records and IDs. On a
  disposable copy, an explicit conditional with matching record exports removes the
  diagnosed compiler failure and the contradictory sequential URL guards. This is
  structural work, not authority granted to the new value-only follow-up. Compilation
  does not validate the remaining learned copying, selection, unrequested actions or
  product facts.
* **Code review:** the exact original proposal reports
  `REQUIREMENTS_OUTPUTS_CHANGED /outputs/reviewJson`. A deliberately tested standalone
  non-null assertion does **not** change that exported nullable type in v15; the
  diagnostic correctly remains. The new routing avoids granting unrelated cleanup
  changes, but does not invent a nonnullable producer or certify this composition.
  A reviewed structural adaptation remains necessary when no valid existing binding
  can satisfy the interface.

Neither retained session was advanced, approved or executed. The minimal replay is
separate from the complete local fixtures below; their success is not live acceptance.

## Deterministic correction and independent execution

[Product fixture](product-corrected-taskplan.json), [actual v15 YAML](product-corrected-v15.yaml),
[binding defect](product-binding-defect.json), [correction trace](product-correction-trace.json),
and [independent outcomes](local-followup-results.json).

The first scripted response omits requirements. Initial regeneration supplies the
accepted requirements and the fixture's invalid `choice` export; one scoped patch
corrects that export. It reaches review with **3 planning calls / 2 corrections**,
without approval. Estimated complete request allowances are **21,524 → 21,858 → 4,692**;
the third request contains the exact value slot. These are the existing conservative
request estimates, not provider tokenizer measurements. Paid cost is zero; no pricing
basis or monetary estimate is invented.

Real local Browser/Document execution checks ten visits, all 39 mapped pages,
12 offered candidates including candidates outside generation examples, original
action arguments, exact headers/cells/order, permitted blanks and Browser closure.
Typed copying, reconnection and row construction use zero inference. Runtime decision
and extraction calls use deterministic adapters. Committed recovery adds no inference,
navigation or writing.

Complete and repeated-selection fixtures pass the unchanged live oracle. The missing
facts fixture passes exact fixture-ground-truth cell assertions; the live oracle still
conservatively rejects its absence claims. Incomplete acquisition stops before product
mapping and workbook writing. No assertion or oracle was weakened.

[Local code-review proposal](local-review-proposal.json), [actual v15 YAML](local-review-v15.yaml),
and [results](local-review-results.json) independently exercise a local clone, checks,
exact feedback, report writing, truthful cleanup and recovery. Remote services are
deterministic adapters. Seven variants cover success/findings, failed checks,
incomplete evidence, denial, publication failure and uncertain completion. This is a
compatibility fixture, not the retained remote review or its approval.

## Validation

Owning gates: 67 routing checks, 119 continuation/correction checks, 8 normal-host
persistence cases and 27 host/local-execution scenarios passed with `-warnaserror`.
Historical scopes, unrelated-change rejection, pending receipt recovery, tenant
isolation and repeated polling are covered. Initial expected regression failures
are retained in the local validation bundle.

Full serial solution, Release/package, Native AOT, published encrypted recovery and
candidate CI results are reported separately on [PR #117](https://github.com/GnouGo/GnouGo/pull/117).
The planning skill
passes its validator. No source change to the compiler, executors, mapping behavior,
public schemas or resource ceilings is included.

## Remaining limits

Scripted responses establish correction mechanics and exact local execution, not
future model compliance. A correctly scoped request can still return an invalid or
semantically unsuitable answer. Structural business changes must preserve accepted
intent or receive explicit review. Neither retained live workflow has achieved
business acceptance. PR #117 remains draft.
