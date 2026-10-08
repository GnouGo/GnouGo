# Amazon artifact review — empty acquisition correction

Candidate: `e04173619b3a3444586cc185fb3e7f64cb4dd820`.
Run: `emptyacquire20261008a-amazon-1`. Revision: **4**.
Artifact: **`c243211195ab1f815c0464005fbc45c4c11abd922a54f9b7330466059ef4f028`**.

[Generated YAML](amazon-r4.yaml) · [TaskPlan](task-plan-r4.json) · [Compiler review](review-r4.json)

## Accepted requirements and actual work

| Requirement ID | Implementation to review |
| --- | --- |
| `search_amazon_fr` | `openAmazonHome` obtains a complete Amazon.fr observation; observed-reference consent is conditional, then a fresh observation supplies the search field. `enterSearchQuery` submits the public `query`. |
| `collect_up_to_10_products` | `observeSearchResults` obtains a complete snapshot; `extractUpTo10ProductLinks` requests the first ten observed products. The sequential visit loop has a checked `maxItems: 10`; oversized selection fails instead of silently truncating. |
| `visit_each_product` | Each loop iteration calls `openProductPage` with the selected observed URL, then `extractProductFacts` consumes that page's complete observation. A missing URL fails its assertion before navigation. |
| `explicit_missing_data` | Product fields permit null; extraction objectives forbid invention and request explicit missing-data status. Detected CAPTCHA and missing required action references stop through assertions. Browser `OBSERVATION_EMPTY` is an error, not evidence of CAPTCHA or absent products. |
| `save_excel` | `compileRows` prepares TSV, then the real Document `document_write` creates `workflows/schema-portability-20261002/emptyacquire20261008a-amazon-1/products.xlsx`. The final path comes from that operation's result. The unchanged oracle independently checks the actual workbook against observations. |
| `close_browser_always` | Root finalization explicitly calls `browser_close`, including verified failures. Uncertain external completion still blocks unsafe cleanup/replay. |

## Explicit limitations

This artifact retains interpretation of full observation snapshots, product selection/extraction, and TSV formatting. It does not introduce the separately deferred removal of formatting inference or duplicate analysis. Runtime request admission can reject a large observation before inference; no token limit is raised and no observation is truncated. Model-selected references are checked by Browser against the actual observed element and requested action; that establishes action compatibility, not business correctness.

The original proposal used nine nonboolean `requires` values. One scoped revision changed only those assertions to explicit predicates. A structural comparison confirms all operations, objectives, conditions, data bindings, public interfaces, ordering and cleanup remained identical. Static compilation now passes; this is not evidence of successful execution or complete business correctness.

No Amazon execution has started. Approval must explicitly acknowledge all six requirement IDs and this exact revision/hash. Existing permissions and the EUR 150 campaign ceiling remain unchanged. Historical approvals are not reused.

## Planning and readiness

- Three logical planning calls, four physical attempts, one explicit revision, zero automatic repairs, two discovery reads.
- 29,245 input and 6,540 output tokens verified; EUR 0.3038645843 planning cost; 401.478 seconds cumulative planning latency.
- Campaign upper bound: EUR 112.9854556947, including unchanged EUR 5.2033274578 unknown reservations.
- Configured pricing/currency readiness and the disposable Browser/Document/XLSX readiness probe passed.
- Focused Browser: 136 passing; local execution/receipt cases: 38 passing; self-contained published Browser encrypted receipts: 5 passing.
- Full solution and remote CI are checked separately before delivery.
