# Concrete Amazon artifact review

Run `adaptive20261007a-amazon-1`, frozen production/harness `05d1321634d94742f10e56896dcb2eb3a9e5271b`, revision **4**, artifact **934120a41f51db1aa5ba4d80282e59539f5d9392e56b7b14b5e0c595a6cb39f3**.

The user authorized one fresh Amazon E2E in the accepted implementation plan. This manual review applies that authorization to this artifact only, under the unchanged EUR 150 campaign ceiling. Requirement acknowledgments were checked individually against operations, bindings, iteration and finalization; they were not generated automatically. Historical approvals and uncertain executions are untouched.

| Accepted requirement | Reviewed implementation |
| --- | --- |
| `search_amazon_fr` | `build_search_url` explicitly interprets the sole public query; `open_search_page` performs navigation with complete acquisition. `compact_initial_pages` independently extracts every page. `interpret_initial_page` selects a consent reference from compact observed controls; `accept_cookie_if_present` uses a direct non-null guard and requests `activate`. Browser resolves the exact observed element and checks action compatibility. A fresh complete acquisition follows. |
| `collect_first_products` | `compact_search_pages` processes every observed page. `select_first_product_links` selects at most ten observed URLs globally from compact facts and original completeness metadata. The sequential loop enforces `maxItems: 10`, concurrency one. |
| `extract_product_details` | Every loop iteration performs `open_product_page`, independent `compact_product_pages`, then explicit interpretation of observed facts into nullable name/description/price plus a truthful status. The loop exports its explicit product port. Complete acquisition must succeed; missing data and CAPTCHA remain explicit and the execution oracle independently verifies values and visits. |
| `save_excel` | `prepare_workbook` uses the collected rows and search summary for explicit TSV interpretation. `write_excel_file` calls the real Document writer at the fixed authorized products.xlsx path. Public filePath is its returned result, not a literal standing in for execution. |
| `close_browser` | The root finalizer invokes `close_browser_cleanup` after success or verified failure. Unknown completion continues to prohibit cleanup and replay. |

Three extraction declarations use `each: { input: pages, output: pageFacts }`, with canonical item and no unrelated shared raw snapshots. New compiler bindings disclose adaptive inference: generic first, specialization only of unresolved items, possible per-item inference, one existing cumulative finite runtime/campaign budget, complete validation before publication. The harness enforces its existing 30-minute runtime ceiling and campaign spending gate. These are not claims of semantic correctness or execution success.

Visible interpretation remains for URL construction, consent decisions, product selection/interpretation and TSV formatting. Global inputs are compact facts plus producer metadata; no raw snapshots are passed again. The independent unchanged oracle still checks observed contents, actual product visits, workbook cells and cleanup. No permission, token ceiling, timeout or oracle was relaxed.

Revision 2 was rejected before execution: raw snapshot extraction, invented classification/status requests and an unobserved selector. One explicit revision preserved every accepted requirement while replacing those compositions through existing planning forms. Both proposals and feedback are retained.

Before execution: three logical planning calls, four physical attempts, two discovery reads, zero automatic repairs; 37,079 verified input tokens and 11,839 output tokens; EUR 0.479692 verified cost plus EUR 1.298287 retained unknown reservation; 514.243 seconds planning. YAML: 46,195 bytes / 1,294 lines. Requirement review is human assessment, not proof of business completeness.
