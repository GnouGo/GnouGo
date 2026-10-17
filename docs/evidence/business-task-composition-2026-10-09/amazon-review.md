# Amazon execution review — revision 8

Run: `businesstasks20261009a-amazon-1`  
Frozen candidate: `1022ed33437179386eb8a43c5d9f3cbf11453bfd`  
Artifact: `47248097b19347e131a9c1e98483ebe281f86df726a7d48f538784237d60f0e4`  
Status: `final_review`; zero compiler diagnostics; execution has **not started**.

Review the [actual TaskPlan](amazon-r8-plan.json), [compiled YAML](amazon-r8.yaml)
and [accepted requirements](amazon-requirements.json). No acknowledgment is
preselected or recorded by this document. This approval applies to one execution
of this exact revision/hash, not earlier artifacts.

## Accepted requirements alongside actual work

| Required acknowledgment | Actual composition |
| --- | --- |
| `search_amazon_fr` | `locate` derives the Amazon.fr search URL from the sole public `query`; `read_search` actually opens it and requests a complete observation. |
| `collect_products` | `extract_choices` processes every complete page into only observed selector identity/text arrays. `choose` sees their flattened compact view plus query and returns at most ten IDs in source order; no sponsored filtering or deduplication. |
| `visit_product_pages` | `verified_ids` first validates selected IDs against offered candidates. `visit_products` then looks up original records by selector and navigates to each original observed `href`, in order. Repeated IDs remain repeated. |
| `extract_product_data` | Every product's complete pages feed independent extraction of observed name/description/price string arrays; `reconcile_product` receives those compact facts, the original URL and producer errors. Products are non-null; accepted missing fields remain nullable. |
| `save_excel` | `format_workbook` explicitly formats at most ten compact product records as TSV; `write_workbook` calls the real Document writer. `xlsx_path` comes from its declared `filePath` output. |
| `close_browser` | The root finalizer invokes Browser close after success or verified failure, with existing unknown-completion protection. |
| `truthful_status` | Local assertions check successful acquisitions, non-null snapshots and both truncation flags. A non-null observed search error stops before visits/writing. `completed` is the writer's verified success after required preceding work. Independent oracles still decide factual success. |

Input: `query = "chaussure geox homme 45"`. Output:
`workflows/schema-portability-20261002/businesstasks20261009a-amazon-1/products.xlsx`.
There is no consent action, unsolicited page interaction, runtime startup
confirmation or GitHub publication in this artifact. Browser permissions and
navigation confinement remain enforced. Each acquisition retains its existing
30,000 ms timeout; the earlier live timeout is not claimed resolved.

## Data flow and bounded inference

Thirteen tasks, zero `value` forwarders, two necessary pure projection loops:
`originals` exports existing page record arrays; `verified_ids` exports identities
only after offered-candidate lookup. These are typed deterministic projections,
not inference or additional business actions. The remaining loop actually visits
products. Pure copying never uses learned mapping.

Global interpretations receive query, compact candidate identity/text, compact
product facts, or at most ten reconciled products. They receive no raw snapshot.
Both mapping bindings retain adaptive specialization within the shared finite
runtime/campaign budget. All source pages are processed and validated; examples
do not limit coverage. No token or collection ceiling changes. Source observations
and original URLs remain separate from selection. Final schema validation proves
shape, not factual correctness; reconciliation and TSV contents require the
independent workbook/page oracle.

Compiled artifact: 56 total steps (including nested bodies), 33 sets, three
workflows, 79,833 UTF-8 YAML bytes / 1,592 lines. This live proposal is distinct
from the neutral before/after fixture comparison (61→60 steps, 39→36 sets).
The final targeted revision altered only the authorized extraction result type,
its compact consumer input and the visit precondition. Requirements, operations,
ordering, outputs and cleanup are unchanged from revision 6.

Planning so far: five logical calls / five physical attempts, two discovery reads,
three explicit revisions, zero automatic repairs; 74,373 input / 16,987 output
tokens, 281,807 ms, EUR 0.782212. All five calls have known usage. Execution calls
and oracles remain unstarted. Campaign upper bound: EUR 121.578871 / 150,
including EUR 6.501615 in unchanged historical unknown reservations.

Execution requires explicit acknowledgment of all seven requirement IDs above,
bound to revision 8 and the exact hash. After approval, run once and independently
verify actual navigation/product visits, observed workbook cells, output location
and Browser closure. Preserve any failure; never replay an uncertain invocation.
PR #117 remains draft; one successful execution would not establish all six gates.
