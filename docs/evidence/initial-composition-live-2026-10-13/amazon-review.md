# Initial composition: execution approval review

**Reviewable, not executed.** The scoped structural revision is complete. Approval
of this artifact is separate from authorization to revise it.

| Review identity | Value |
| --- | --- |
| Run | `initialcomposition20261013a-amazon-1` |
| Revision | **9** |
| Artifact hash | `fc751e55508433f9e32a3851f64ef45b49a6502ea338a970dcf173b43821e4dd` |
| Frozen candidate/harness | `bd83e46e61667ce846683cf01f34f3ea292c30ca` |
| Profile | `compact-bindings-v15` |
| Input | `query = chaussure geox homme 45` |
| Product ceiling | **10**, sequential |
| Destination | `workflows/schema-portability-20261002/initialcomposition20261013a-amazon-1/products.xlsx` |

The complete [TaskPlan](taskplan-r9.json), [actual compiled YAML](workflow-r9.yaml),
[accepted requirements](requirements.json), [review comparison](review-r9.json)
and [zero-inference recompilation](compilation-r9.json) are retained together.

## Requirements to acknowledge

1. **`amazon_search_and_product_extraction`** — Search Amazon.fr with the supplied
   query, visit up to the first ten offered products in order, and preserve observed
   facts and genuine missing values without invention.
2. **`xlsx_saved_to_requested_path`** — Write a real XLSX at the stated path with
   the header `name, description, price, url, status, error`, followed by typed
   product rows in visit order.
3. **`browser_cleanup_and_truthful_status`** — Close the Browser in cleanup; final
   success requires the actual writer's success and returned file path.

## Concrete behavior reviewed

- The bounded query-to-search-URL interpretation remains. The Browser acquires
  complete observations; producer success and both truncation flags are checked
  before adaptation.
- Both extractions process complete pages independently and return one compact
  candidate array per page. Explicit one-level flattening supplies the global
  decisions. Complete observations and authoritative action URLs stay separate.
- Product selection returns unchanged IDs. A strict lookup checks offered
  membership before another strict lookup retrieves original navigation records.
  Repetitions and order are preserved; no additional actions or selection criteria
  were introduced.
- Every selected product receives its own Browser navigation. Fact interpretation
  sees compact candidates and returns nullable IDs only. Each present fact ID is
  resolved against offered candidates and original records before copying text.
  A complete characteristics block can be a description without a heading; null
  remains permitted for genuine absence or uncertain subject association.
- Product records, nullable branches, rows, headers and status strings are assembled
  deterministically. Names, descriptions and prices come from original text; URLs
  come from original records. Status strings are JSON text of actual producer
  success booleans, not model-written assessments. Missing fact cells stay null.
- Writer rows are `flatten([[headerRow], productRows])`. Its path and boolean
  success supply the public outputs, after local writer assertions. Browser close
  remains in the original cleanup scope.

No inference is used for typed copying, lookup, status conversion or row assembly.
There are five inference declarations: query conversion, search extraction,
product-ID decision, product extraction and fact-ID decision. The latter two run
inside the bounded visit loop; actual mapping calls/cache hits are not yet known.

## Verification and limits of this review

The final plan exactly matches the compiled offline correction. All three accepted
requirements, four external operation identities/order/literal arguments, three
public outputs, the ten-product ceiling and cleanup are preserved. Compilation has
zero diagnostics; recompilation reproduces identical YAML. The artifact contains
**28 tasks, 28 compiled steps, 4 sets, 9 workflows and 289,190 YAML bytes**.

The frozen candidate passed **5,502 solution tests**, no failures, 13 existing
skips; 29 applicable CI checks; Release/package, Native AOT, encrypted recovery,
skill and local independently inspected Browser/Document gates. All 709 pinned
binaries are unchanged. This continuation changes no production code.

Those checks do not prove the live model will select all correct candidates or facts.
The rejected r5 and r7 responses remain recorded. Initial generation compliance
and the new artifact's business acceptance are not established by compilation.
The unchanged independent oracle must inspect complete acquisitions, actual visits,
original text, XLSX headers/cells/order/location and cleanup after execution.

## Accounting and authorization boundary

The same session has consumed **6 logical planning calls / 7 physical attempts**,
one automatic repair and three explicit revisions. Known planning cost is
**EUR 0.887653**. Its **EUR 1.308506** uncertain-attempt reservation remains intact.
The campaign upper bound is **EUR 147.437524 / 200**, with **EUR 52.562476** remaining
after all reservations. Pricing/currency readiness passed; no ceiling was changed.

No execution has started. Approving this review authorizes **one execution of this
revision/hash**, acknowledges the three requirements above, and preserves existing
permissions, runtime inference limits, independent oracles and the EUR 200 ceiling.
Historical approvals do not apply. PR #117 remains draft pending real acceptance.
