# Windows qualification for PR 120

The two workbooks contain real Amazon.fr observations for `chaussure geox homme 45`. The report reviews SlimFaas PR 519 at head `458efb736ad6f4159281606da573d51c5115256c`. All three qualifications used `OpenAi / gpt-6.1-terra`.

| Execution | Result | Files |
| --- | --- | --- |
| One-product diagnostic | One separate product visit. Document MCP wrote and reopened the workbook. An independent ZIP/XML reader matched all three cells to the source records. | [Workbook](first-product.xlsx) · [Readback](first-product-readback.json) |
| Ten-product diagnostic | Four earlier source resolutions, then ten separate product visits in search order. All 30 cells matched their source records. | [Workbook](ten-products.xlsx) · [Readback](ten-products-readback.json) |
| Local code-review qualification | The model reviewed the pinned diff. Two findings were verified in local fixtures. Both the report and the decision stayed local to the review. | [Report with changed-line references](slimfaas-519-review.md) · [Checks and receipt](slimfaas-519-receipt.json) |

Each Amazon diagnostic encountered one source replacement before selection. Browser withheld the first capture's navigation authority. The diagnostic retained that capture separately, started a fresh explicit search and selected products only from the new observation. The order verifier examined the sequence beginning at that fresh search. Both diagnostics closed Browser and verified that a subsequent page read failed. The workbooks came from these diagnostic runs, independently of the Server planning sessions.

The published Browser on `feed56b5` exposes the source publication decision in `observedSourceAccepted`. Its loopback test also verified the compact detail text against the original record and preserved the selected URL's encoded query. All 20 observed-navigation permission tests passed. [Source and wire checks](browser-source-receipt.json)

The first five Server sessions on this grant stopped during planning. The third reached local acceptance of the selection and detail-visit blocks. The fifth reached local acceptance of the selection block, then retained a loop that could stop before reporting a returned failure. None of the five reached final approval or business execution. [Compound drafts](compound-draft-replay.json) · [Detail projection](detail-projection-replay.json) · [Document path](runner-document-paths.json) · [Repeated branch review](repeated-branch-review.json) · [Returned collection](returned-collection-review.json)

The €15 grant is shared across both scenarios. The financial ledger uses measured tokens and captured tariffs. The predecessor campaign retains its own balance and records. [Authorization and request 90 closure](funding.json)
