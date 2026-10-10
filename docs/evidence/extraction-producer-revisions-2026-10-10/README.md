# Extraction declarations and responsible producer revisions

Candidate: `96cbf217`, PR #117 (draft), from `a23beb8f`.
Historical `nativesnapshots20261010a-amazon-1` artifacts, receipts and reservations
remain unchanged. No historical workflow is resumed.

## Defect and correction

The retained generation bound `pages` but declared `each.input = page`, declared
flat records where its consumer used one-level flattening, and used an operation
identity as a business `choice` in an immutable assertion. Consumer-only repairs
could not fix those declarations. The previous context omitted the named inputs
of a fixed extraction producer.

Fresh generation and repair guidance now distinguishes bound input names from
item aliases and complete assembled result types from per-item types. Selection
returns IDs and reconnects offered candidates and original action data through
existing lookup bindings. Instructions retain requested work without adding
unrequested goals, effects, filtering or deduplication.

Existing diagnostics identify the declaration, bound input names, output shape
and producer. Invalid extraction declarations and invalid choice references in
immutable `requires` yield `REVISION_REQUIRED` before a new automatic call, even
when an unrelated consumer slot is editable. Explicit existing `EditablePaths`
revise the responsible declarations and assertion. Read-only extraction producer
context includes typed input references, without observations or edit authority.
An already-issued request retains its prompt/schema/authority: its committed
response is processed first, and an invalid whole-plan patch remains atomic.

There is no compiler acceptance, public schema, runtime, mapping, persistence,
permission, limit, compilation-profile or repair-authority change.

## Deterministic evidence

The parameterized [regression](../../../tests/GnOuGo.Flow.Planning.Tests/ExtractionProducerRevisionTests.cs)
retains the faulty composition and corrected counterpart with arbitrary names.
The original commit is tested in an isolated checkout, without inference. The
corrected composition reaches review in one call with no repair; a faulty producer
requires an explicit three-slot revision, reaching review in two cumulative calls
and no automatic repair.

The real [Browser/Document fixture](../../../tests/GnOuGo.Agent.Server.Tests/LocalProductOutcomeExecutionTests.BusinessTasks.cs)
executes that targeted revision through the normal request schema and review gate.
Its independent oracles inspect actual local product visits, record ordering,
nullable missing price, writer result, workbook cells and Browser cleanup. Eight
execution variants pass, including repeated IDs, empty input, unknown candidate,
incomplete observations, denied writing and fabricated-output detection. The
fixture checks complete page coverage and compact serialized inference inputs;
typed copying requires no inference. Receipt restart causes no additional visits
or inference.

The producer-revision case visits ten products and consumes 38 complete pages.
Its twelve offered candidates include candidates outside generation examples; the
serialized decision view is 2,597 bytes. It uses two deterministic planning calls,
zero automatic repairs, eleven mapping calls and 24 total runtime adapter calls.
The 13-task plan compiles to 75,102 YAML bytes and runs 225 normal steps plus one
cleanup step; its inspected logical journal is 22,622,687 bytes. These are local
fixture measurements, not live-provider usage or new performance claims. See
[execution measurements and oracles](local-execution-oracles.json).

The actual compact-schema prompt is tested. An initial longer wording exceeded a
retained 24,000-token discovery gate; the wording was shortened until the same
assertions passed, without increasing the allowance or removing contracts.

## Validation and live status

The final solution passes **5,080 tests across 33 assemblies**, with 13 existing
platform/live skips and zero failures under `-warnaserror`. This includes 1,236
Planning, 751 Agent.Server, 1,187 Flow and 20 Persistence passes. See
[per-assembly totals](solution-tests.json). Planning Release packaging, Native AOT
smoke (including typed patch recovery), skill validation and the frozen harness
build pass without warnings. `SkipModelMetadataGeneration=true` skips network
metadata generation, not tests. All 29 non-skipped applicable CI checks pass (four publication/optional checks skipped); see [CI results](ci-checks.json).
The superseded `f7cad0f0` CI exposed two nullable test warnings and omitted
compatible-alternative guidance. Both were corrected, the complete Planning suite
rerun, and the new candidate frozen before any paid dispatch. An interrupted
preliminary solution run is not counted as final validation. All gates and
configured pricing/currency readiness passed before the fresh paid session began.

## Fresh live planning — execution awaiting approval

`extractproducer20261010a-amazon-1` is frozen on the validated candidate above.
The first proposal stopped for changing the accepted product-record nullability.
Two authorized limited structural revisions corrected the adaptation composition
and removed unrequested work. A final six-slot targeted revision preserved all
business operations while moving authoritative URL copying into a typed export
and enforcing completeness before extraction. The rejected proposals, compiled
artifacts and exact revision commands are retained separately; see
[review history](amazon-review-notes.md).

Revision **8** has no compilation diagnostics. It binds each extraction to the
actual `pages` collection, assembles per-page arrays before flattening, supplies
compact facts to interpretation, and validates chosen IDs against offered
candidates before resolving original observed records. Action arguments and the
exported product URL come directly from originals/producer outputs. Accepted
requirements and non-null product records with nullable fields are unchanged.
The writer and root cleanup remain actual operations.

[Concrete artifact review](amazon-review.md) records revision 8, hash
`e6b81b19d8b3c4ccacad4a112ab5e5f4838fd58bdc84e78b4464b28b3e9d41ad`
and all five requirements. The review was presented for explicit acknowledgment;
no artifact approval or execution has been recorded. The plan has 28 tasks and
compiles to 91 steps / 56 sets / 142,616 YAML bytes. These are artifact measurements,
not a size-reduction claim or observed invocation counts.

Planning: five logical calls, six physical attempts, three explicit revisions,
zero automatic repairs, two discovery reads; 68,724 input and 21,233 output tokens;
**€0.870184**, 572,345 ms cumulative active time. All new requests have receipts,
with no new unknown attempt. Campaign upper bound is **€123.881383 / €150**, keeping
the historical €6.501615 of unknown reservations. See
[planning and accounting](amazon-planning-r8.json).

Execution: **not started**. Live navigation, extraction, XLSX cells and cleanup:
**unverified**. The authorized evaluation remains one execution, at most ten
products, after its revision/hash-bound approval and explicit requirement
acknowledgments. No code-review run, cohort expansion or historical replay occurs.
PR #117 stays draft; planning success does not establish execution success.
