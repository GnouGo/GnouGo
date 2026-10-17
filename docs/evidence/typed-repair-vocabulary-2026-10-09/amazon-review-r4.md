# Amazon revision 4: compilation passes; execution is not approved

- Frozen candidate: `99cc3dcaa56755be2de7de7cafb6ffd32b7a5e11`.
- Run: `repairvocabulary20261009a-amazon-1`.
- Revision: **4**.
- Artifact: `8f1122e4cd05b017b249469094bef9e8a5399d5096210718d933c168c703a6eb`.
- Planning: three logical calls, four physical attempts, one explicit targeted revision, zero automatic repairs, two discovery reads. All calls have receipts; this run has no unknown usage.
- Cost: EUR 0.267522. Campaign upper bound: EUR 119.283978 / 150, including the unchanged EUR 6.501615 of historical reservations.

## Accepted requirements and actual work

| Requirement | Actual composition | Review |
| --- | --- | --- |
| `success`: search from `query`, visit up to ten products, write observed name/description/price to the fixed XLSX path | Construct search URL; Browser complete acquisition; extract product links; sequential per-product Browser acquisition and extraction; bounded TSV formatting; Document writing; writer-derived output path | Operations are present, but the adaptation defects below prevent execution approval. |
| `blocked`: report an observed blocker without declaring success | Extracted search blockage flag and interpreted per-product blockage flag feed explicit runtime `requires` | Search extraction asks a source-grounded mapper to classify blockage; the evidence contract for that classification is insufficient. |
| `cleanup`: close the Browser even on failure | Explicit Browser close in root `always` | Preserved. |

There are no cookie acceptance, refusal or customization actions. There is no sponsored-item filter or deduplication. The public input remains `query`; the output interfaces and accepted requirements are unchanged.

## Targeted correction that succeeded

The initial proposal failed `REQUIREMENTS_OUTPUTS_CHANGED` at `/outputs/products`. A read-only replay compiled the plan, while its public output contract still failed full planning validation. The singleton-row array followed by flattening did not establish the accepted output collection bound.

The explicit revision changed only three authorized sites: `extract_product_row.resultType`, the corresponding loop export, and `flatten_rows.rows`. A row is now a non-null object; the bounded foreach collects those objects directly. No business operation, requirement, objective, guard, order or cleanup changed. The exact patch was accepted through the real version-10 request and reached `final_review` without another discovery or automatic repair.

## Remaining review blockers

1. `extract_search_products` and `extract_product_row` receive whole `observationSnapshot` values without independent `each` processing. Their target contracts also mix source extraction with business selection or an undeclared `ABSENT` fallback.
2. `detect_product_blockage` interprets the complete product snapshot and content. Its immediate decision does not require the whole retained observation. The workflow does not contain the bounded extraction and compact-view composition needed before this interpretation.
3. Search selection returns URLs directly, without the explicit offered-candidate-ID selection and deterministic reconnection checked by the retained execution strategy.

These findings are a manual composition review, not a claim of deterministic proof over prose. No runtime input size or live page availability has been observed because Browser execution has not started.

Correcting these points requires inserting/restructuring data-adaptation tasks through the existing explicit structural revision mechanism; the three authorized value/declaration slots cannot add those tasks. No artifact approval is requested for this revision. The saved proposal, YAML, receipts and reservations remain intact, and no uncertain invocation is replayed.

Execution and independent live navigation/XLSX/cleanup oracles: **not run**. Local deterministic Browser/Document execution evidence is recorded separately and is not live acceptance.
