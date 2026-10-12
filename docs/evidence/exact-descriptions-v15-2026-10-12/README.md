# Exact descriptions and local v15 simplification

Baseline `616d5071`, PR #117. Description guidance is a separate correction (`e47e6b10`); compiler changes use `compact-bindings-v15` solely to preserve historical compilation and approvals. There are no executor, expression-metadata, sandbox, persistence, permission or budget changes.

## Description-selection result

The retained third product has 15 complete pages, 585 original records and 44 offered candidates. Five offered blocks contain complete relevant descriptive features. The committed interpreter response selected `descriptionId: null`. Strict lookup and the writer preserved that decision; the historical workbook's unsupported absence remains an acceptance failure. The evidence establishes what was available and selected, not the model's internal reason.

[The independently frozen acceptable references](expected-description.json) precede replay. The [public encrypted-storage replay](replay/Program.cs) checks the committed decision, original observations, offered candidates and text hashes. All five IDs reconnect to their exact original complete text; source bytes remain unchanged. [Results](description-replay.json): zero provider calls, zero MCP calls, zero historical writes, no workflow resumption.

Fresh generation/revision guidance and the existing exact-block example now say that complete feature/characteristic text can be descriptive without a heading. Choose one relevant offered block unchanged; multiple suitable blocks alone do not justify null. Genuine absence and uncertain subject association still permit null. Controls are not descriptive evidence. No semantic validator, null rejection, additional repair or runtime heuristic is introduced.

Tests cover headed and unheaded descriptions, several suitable blocks, controls, absence and uncertain association. Actual emitted generation/revision/repair requests contain the instruction and remain within existing admission budgets. A real local Browser/Document execution writes the selected heading-free block verbatim and independently inspects the workbook. These are deterministic adapters: they prove guidance coverage and exact execution, not future model compliance.

## Identical retained TaskPlan: actual generated artifacts

The [v14 YAML](generated/live-r7-v14.yaml) is byte-identical to the saved approved artifact. The [v15 YAML](generated/live-r7-v15.yaml) compiles the same TaskPlan against the same original contracts. No composition or live-session revision is involved. All validation origins remain present; historical approval verification succeeds. [Reproduction and measurement tool](compiler-replay/Program.cs), [raw metrics](compiler-measurements.json).

| Metric | v14 | v15 |
|---|---:|---:|
| Workflows / steps / sets | 7 / 26 / 5 | 7 / 26 / 5 |
| YAML bytes | 217,493 | 216,379 |
| Unique-expression JavaScript bytes | 19,843 | 19,171 |
| Maximum JavaScript line length | 175 | 163 |
| Declarations | 184 | 179 |
| Arrow functions | 109 | 88 |
| Maximum function nesting | 4 | 3 |
| Identity maps | 8 | 0 |
| Singleton objects (including business objects) | 51 | 48 |
| `observedInputs` / `projectedValues` declarations | 9 | 0 |
| Joint `results/count` objects | 8 | 8 |
| `m.select` | 0 | 0 |

These are syntax counts, not semantic costs. Full physical-read declarations repeated within an expression decrease from three to two; only one read in the candidate-input example is safely reusable. Unknown escapes and joint capture contracts stay conservative. Complete programs and all schemas are retained in the YAML.

### A. Repeated candidate reads, with the assertion retained

Actual [before](examples/candidate-input-v14.js):

```javascript
const candidates = data.steps.n_3c64eb58e6504189.value.candidates;
const requiredCondition = candidates.flatMap(item => item) !== null;
const candidates_2 = data.steps.n_3c64eb58e6504189.value.candidates;
const offeredFactCandidates = candidates_2.flatMap(item => item);
```

Actual [after](examples/candidate-input-v15.js):

```javascript
const candidates = data.steps.n_3c64eb58e6504189.value.candidates;
const requiredCondition = candidates.flatMap(item => item) !== null;
const offeredFactCandidates = candidates.flatMap(item => item);
```

`candidates_2` remains an ordered alias contract referencing `candidates`. Its schema and origin still run after `requiredCondition`, before `offeredFactCandidates`. Both flatten evaluations remain: this local change does not hoist a later calculation over the assertion, invent a constant or introduce a new way to cache validation results.

### B. Collection assembly

Actual [before](examples/product-assembly-v14.js) creates `items` through `(flattenedMatchedRecords => ({ results: ..., count: ... }))(flattenedMatchedRecords)` and then evaluates `(items.results).map(item => item)`.

Actual [after](examples/product-assembly-v15.js):

```javascript
const items = {
  results: flattenedMatchedRecords.map((item, index) => ({
    nom: (item).nom,
    description: (item).description,
    prix: (item).prix,
    url: (item).url,
    erreur: (item).erreur
  })),
  count: flattenedMatchedRecords.length
};
const products = items.results;
```

The already checked array replaces the identity map. The `items` joint contract and products' later bound/nullability checks remain separate and ordered. `results/count` and the final checked-set wrapper remain because removing them would require decomposition of existing joint contracts. They are not claimed to be universally irreducible.

### C. Nullable strict lookup

The complete [before](examples/nullable-lookups-v14.js) and [after](examples/nullable-lookups-v15.js) show all three fact branches. Capture names now identify their business purpose (`descriptionId8PageRecordsInputs`); generated result names retain collision-safe suffixes. A singleton forwarding IIFE becomes its direct expression when single evaluation and schema equivalence are established.

The selected description branch still validates offered membership before original lookup, then copies `block.text`. The alternative still returns permitted null. Multi-field capture objects keep their complete joint schemas, physical field addresses and original origins. Conditional blocks remain because they determine which validations execute. No permissive `find`, null-coalescing fallback or shortened ID replaces lookup.

Actual selected-branch excerpt before:

```javascript
const matchedRecords_8_0 = m.lookup(flattenedPageRecords_8_0, descriptionIdList2_8_0, "reference");
const items_8_0 = (matchedRecords_8_0 => ({
  results: matchedRecords_8_0.map((item, index) => item.text),
  count: matchedRecords_8_0.length
}))(matchedRecords_8_0);
const text_8_0 = items_8_0.results.map(item => item);
```

Actual excerpt after, at the same point after offered membership validation:

```javascript
const matchedRecords_8_0 = m.lookup(flattenedPageRecords_8_0, descriptionIdList2_8_0, "reference");
const items_8_0 = {
  results: matchedRecords_8_0.map((item, index) => item.text),
  count: matchedRecords_8_0.length
};
const text_8_0 = items_8_0.results;
```

The empty capture object in the null alternative also remains: its original call-input check has its own conditional execution point. Existing alias metadata needs an evaluated local with the same value; it cannot replace that object check with a check on `[null]`. Likewise, port wrappers with generic/coupled object contracts stay intact. Removing them would need a stronger equivalence proof, not deletion of their checks.

## Every retained set

No set disappears in this local correction. Their existing boundaries remain stable; the changed work occurs inside their expressions and consumer inputs.

| Physical ID | Producer / consumers | Retained responsibility |
|---|---|---|
| `n_8cc9a567ea590ba6` | Query input → URL transform's template/inference | Required nonempty-query assertion before request rendering. This correction does not change input-fusion eligibility or move the assertion into inference. A broader consumer-fusion change could reassess it; an existing receipt alone is not the reason to retain it. |
| `n_012c777eb040657b` | Search acquisition → page extraction and original-record resolution | Ordered acquisition/completeness checks and a shared original-record view. Removing it would require proving shared evaluation, source association and check timing across inference. |
| `n_351e8c8fdc59fd92` | Product acquisition → fact extraction and original text lookup | Same early completeness and shared-view responsibility inside each visit. Joint collection constraints remain before interpretation. |
| `n_ab830a8ef9d7f7bf` | Completed visits → writer rows and public product results | Bounded, non-null typed product validation before writing; two consumers require different portions. Moving validation to public outputs would put it after writing/cleanup. |
| `n_bc4542348acd7e9e` | Successful writer → public outputs | Validate writer success/path and assemble the public result before Browser finalization. A post-finalization output cannot preserve that error order. |

## Equivalence, resources and limits

Actual compiler-generated v14/v15 fixtures are embedded in Flow tests. They compare exact large JSON numbers, whitespace, null versus absence, unknown IDs, stricter later checks and all reached validation origins/first errors. Identity-sensitive comparisons retain their copies. Invalid unused intermediates still fail. Existing schema-alternative, cancellation, finalization, mutation and recovery tests remain authoritative.

[Resource samples](resources/) use the unchanged 10,000-statement / five-second / 50,000,000-byte allowance. Cold and warm runs are recorded separately, with one-byte and 1.2 MB unrelated observations. The latter is not imported. On warm successful samples:

| Fixture | Allocated bytes v14 → v15 | Charged materialization v14 → v15 | Statements | Checks |
|---|---:|---:|---:|---:|
| Repeated array reads | 43,552 → 37,808 | 3,364 → 2,670 | 49 → 41 | 4 → 4 |
| Nullable strict lookup, present | 163,744 → 138,984 | 8,323 → 6,781 | 101 → 76 | 7 → 7 |
| Nullable strict lookup, null | 79,120 → 59,704 | 942 → 676 | 35 → 28 | 3 → 3 |

These allocations measure the expression evaluator, not the whole host. Individual elapsed samples are noisy and do not establish a latency improvement. Trace outcomes contain references/origins, not observation payloads. Check order and event cardinality remain identical; source JSON remains unchanged.

The retained-data replay additionally evaluates the **actual saved candidate preparation** from both generated artifacts. Its 333,597-byte source observations remain separate from the 6,240-byte mapping output. All 44 offered candidates are byte-for-byte equivalent as JSON values. Warm allocated bytes decrease **634,760 → 574,888**; charged materialization **287,390 → 241,407**; statements **1,322 → 1,127**. All four original checks run in the same order. Both remain below the unchanged 50,000,000-byte ceiling. This evaluates saved expressions only; no workflow, inference or MCP invocation is resumed.

## Validation and limitations

See [validation](validation.md) for owning tests, real local Browser/Document execution, independent workbook checks, full solution, packages, Native AOT, recovery, skill and candidate CI. Retained historical evidence and approvals are untouched. This work makes no paid calls, live executions or financial changes. The PR remains draft; further live work needs new authorization and artifact approval.
