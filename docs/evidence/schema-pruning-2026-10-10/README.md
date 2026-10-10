# JSON Schema alternative pruning and committed-receipt recovery

Production baseline: `6269577e8b852fa19dde82fecc6d67834ae5d5ca`.
Original frozen live candidate: `7376b4bd713d72d43dbe62270e41823273e0e618`.
Session: `physicaloutputs20261010a-amazon-1`, revision 5 before recovery.

## Correction

The instance validator now rejects provably incompatible `anyOf`/`oneOf`
alternatives before descending into recursive children. It uses explicit types,
constant/enumerated values, present property assertions and local references.
Missing optional tags and unknown/cyclic references retain ordinary validation.
No discriminator annotation gains semantics. Match counting and the existing
selection of detailed versus ambiguous diagnostics are unchanged.

Only `JsonSchemaInstanceValidator` changes production behavior. Planner, compiler,
Browser, mapping, public contracts, budgets and limits are unchanged. The small
benchmark recovery command uses the existing replay adapter, campaign lease,
normal planner advancement and encrypted checkpoint boundary; it creates no
provider or MCP transport and stops after one committed response.

## Offline evidence

The exact retained response and original issued schema remain in
[the original evidence](../physical-output-selection-2026-10-10/).
The new embedded regression validates the `response` payload, not its inspection
wrapper. It preserves both JSON documents unchanged and makes no inference calls.

The first targeted run passed 33 tests. The saved response validated with zero
findings in **18.030 ms**, allocating **13,068,048 bytes** on the test thread.
The prior validator was still running after **11m21s wall / 10m23s CPU** when the
owned process was stopped; that is a lower bound, not a completed baseline timing.
Typed alternative chains at depths 8, 16 and 32 validated in approximately
0.06–0.37 ms. These measurements are local diagnostics, not runtime limits.

`diagnostic-parity.cs` compares complete finding sequences (including order,
pointers, rules and messages) for 12,000 schema/value combinations against the
validator from `6269577e`. There were **zero differences**. To reproduce, copy the
old source with `git show 6269577e:src/GnOuGo.Flow.Core/Runtime/JsonSchemaInstanceValidator.cs`,
rename its class to `LegacyValidator`, and compile the two files in a temporary
net10.0 console project referencing the corrected Flow.Core assembly. This
comparison includes absent tags, ambiguous alternatives, numeric overlap, nulls,
references and cycles. It is test evidence, not a second production validator.

## Recovery boundary

The new command defaults to an in-memory replay. `--apply` explicitly commits the
single resulting checkpoint. It verifies tenant, saved revision, request identity,
original request/schema, receipt and lack of execution/inconclusive closure.
The original source and manifest remain untouched. Recovery records the corrected
validator build and original hashes separately. The prior session/result are
preserved in encrypted recovery history.

The interrupted interval is **681,000 ms**, from the retained process evidence;
combined with the previous **491,965 ms**, it establishes a planning-time lower
bound of **1,172,965 ms** before recovery. The existing 30-minute allowance is not
reset. The interval is applied once with the checkpoint. Calls, receipts, financial
reservations and the €150 ceiling are not changed or recharged.

No new paid repair or workflow execution is authorized by this recovery. Any
remaining semantic diagnostics are reported without trying another model call.
A generated artifact still needs its own concrete approval. Historical uncertain
invocations and the original live harness's frozen-build checks remain intact.

Full validation and actual recovery results will be recorded separately below.
