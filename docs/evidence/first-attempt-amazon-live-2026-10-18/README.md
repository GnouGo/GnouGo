# Amazon live evaluation — 18 October 2026

**The approved first proposal failed live execution. Amazon's search page loaded,
but the first extraction exceeded the retained input allowance and was blocked
before inference dispatch. No product pages were visited and no workbook was
created. Browser cleanup passed the independent check.** The first submitted
proposal had reached FinalReview without diagnostics or repairs; structural
validity did not establish executable business correctness.

The user requested “now do live amazon test.” This follow-up uses one fresh Amazon
run under the existing campaign and its EUR 200 cumulative ceiling. It is separate
from the [inconclusive baseline/candidate comparison](../first-attempt-taskplan-live-2026-10-18/README.md)
and does not complete the three-repetition comparison or establish the 25% live
improvement gate.

## Frozen setup

- Run: `firstattempt20261018a-amazon-1`; cohort: `firstattempt20261018a`.
- Campaign: `schema-portability-20261002`; no budget or usage reset.
- Source: `e880d32e80a268fde77df11fec661aab0793e48b`, a clean detached local
  checkout based on candidate `8168c140ed58361aae7ec1446e9daed580f16251`.
- Production remains unchanged from the validated candidate. Production SHA-256:
  `acc3cf3d4bb09541977384cacd1b3a9c1949f00bd59d33d37ccfa5e3cd2760c3`.
- Harness SHA-256: `28f1f4ee2ebd2d1c8a9fcb8deed79077c0d52b6d8ed48eddef55bbcaa9de1057`.
- Provider/model: `OpenAi` / `gpt-5.5-2026-04-24`, retained configuration and
  pricing, automatic correction enabled, eight planning calls and two repairs.
- Query: `chaussure geox homme 45`; at most ten observed products.
- Fixed output relative to the configured workspace:
  `workflows/schema-portability-20261002/firstattempt20261018a-amazon-1/products.xlsx`.
- Debug build passed with `-warnaserror`, zero warnings and errors. This matches
  the live harness's Browser/Document executable configuration. All 708 relevant
  built files were hashed before dispatch.
- Provider readiness, an actual local Browser read, browser cleanup and independent
  XLSX write/readback passed with zero model calls.

See [freeze](freeze.json), [harness sources](harness-sources.json),
[binary hashes](binary-hashes.json.gz), [readiness](readiness.json) and
[validation](validation.json). No main-branch commit or push was made.

## Retaining the earlier uncertain request

The previous comparison's request had exhausted its captured uncertainty allowance:
two unknown transport attempts, `MaxUncertainRetries = 1`, and an adapter failure
explicitly marked retry-exhausted. The retention command checked only the separate
`MaxAttempts = 4` ceiling and incorrectly refused this terminal state.

A regression reproduced the refusal before the harness fix: one failing case and
ten passing negative variants. The fix recognizes exhaustion only when the captured
policy, journal counts and terminal retryable transport/timeout failure agree.
Missing or inconsistent evidence continues to block retention. Afterward all 88
campaign tests and 42 HTTP policy/recovery tests passed with `-warnaserror`.

The existing command retained the exact earlier request as inconclusive with reason
`http_uncertainty_limit_exhausted`. Its two unknown attempts, full reservations,
request identity, original result and absence of receipts remain unchanged. It was
not replayed and its cost was not converted to zero. This permits an independently
authorized fresh run while retaining the failed comparison slot. See the
[retention record](previous-request-retention.json) and compressed red/green test
logs. This is a harness correction; no production source changed for this follow-up.

## Proposal and concrete review

| Measurement | Result |
| --- | --- |
| Submitted proposals | 1, on logical call 2 |
| First-proposal structural validity | Passed |
| Logical calls / physical attempts | 2 / 2 |
| Discovery reads / repairs | 2 / 0 |
| Planning time | 84,584 ms |
| Known input / output tokens | 19,807 / 6,345 |
| Known estimated new cost | EUR 0.258819 |
| New unknown attempts | 0 |
| Revision | 2 |
| First-proposal business correctness | Failed the independent execution oracle |

The reviewed artifact hash is
`a2de9a6bb7a09752bf40a7faed948911a961f0d3207d0b5444a4cd598123f82e`.
The existing five requirements cover Amazon.fr search, the first ten observed
products in order, workbook creation, browser cleanup and truthful completion.

The plan builds the search URL, reads the search observation, extracts at most ten
product targets, then sequentially reads each product page and extracts its row.
It assembles the header and rows, writes the fixed workbook and derives completion.
An always-run cleanup task closes the browser. It contains seven root tasks, two
loop-body tasks and one cleanup task. Its inspected operations are
`browser_get_content`, `document_write` and `browser_close`.

Declared workbook and cleanup commitments have verified operation presence and
producer lineage. The shared browser operation has ambiguous associations across
the root and loop and remains unverified. Prose completeness, argument correctness,
per-item outcomes and successful execution are not established by these labels.

The generated proposal uses interpretation for URL creation, row assembly and
completion. Exact data fidelity therefore needs runtime inspection and the retained
independent oracle; compatible types and compilation do not establish it. The
proposal has not been silently rewritten to use copying. The independent
`observed-products-v2` oracle remains unchanged.

The user was presented with all five requirements, the input, destination, revision
and full artifact hash, and acknowledged comparison with the original request,
including omissions, extra effects and cleanup. The explicit answer was
“pprove all five requirements and execute”; its intent was unambiguous. The existing
approval command submitted the five reviewed requirement IDs with revision 2 and
the exact hash. The session advanced to approved revision 3 without changing the
artifact. See
[review status](review-status.json) and [planning measurements](planning-result.json).
Private requests, model responses and full artifacts remain in encrypted storage.

## Live execution and diagnosis

| Measurement | Result |
| --- | --- |
| Execution / independent oracle | Failed / failed |
| Execution duration | 19,665 ms |
| Total planning and execution duration | 104,249 ms |
| Execution logical calls / physical attempts | 1 / 1 |
| Execution known input / output tokens | 120 / 110 |
| Execution known estimated cost | EUR 0.003488 |
| Combined known estimated run cost | EUR 0.262307 |
| Successful search-page acquisition | 1 |
| Product-page visits / workbook writes | 0 / 0 |
| Browser cleanup | Independently verified |
| New uncertain attempts | 0 |

The URL interpretation completed and the browser successfully acquired
`https://www.amazon.fr/s?k=chaussure+geox+homme+45`. Its complete observation contained
1,581 records across 53 pages, with both truncation flags false. The compact UTF-8
JSON snapshot measured 1,112,015 bytes; this measurement is not the serialized
runtime request size.

The generated `extract_first_ten_product_targets` task supplied the entire snapshot
to one mapping request. Read-only inspection estimates its prompt alone at
707,090 tokens using the harness's conservative estimator. The retained campaign
input allowance is 96,000 tokens. `CreateDispatchRequest` rejected the extraction
with `LLM_BUDGET_EXCEEDED` and `dispatch_status: not_started`; this extraction made
zero provider calls. The sole billed execution call created the search URL.
Mapping telemetry's one local model attempt is therefore not an additional
physical dispatch or billed token count.

The inspected Browser contract defines `maxRecords` per observation page, not as
a total observation limit. Selecting `maxRecords = 200` and limiting the downstream
product result to ten did not bound the input to this extraction. This confirms a
runtime request-admission failure for the generated composition. It is not evidence
of provider transport loss, CAPTCHA, missing products or inaccurate extracted rows:
none of those outcomes was established.

The workflow's cleanup called `browser_close` successfully. The unchanged oracle
then independently probed the browser and received the expected “No active page”
failure, confirming closure; its subsequent close is an oracle cleanup call. Those
two oracle calls are distinct from the workflow's search and cleanup operations.
The oracle findings are `workflow_execution_failed` and `workbook_missing`.
No final business outputs were returned, and no successful completion was claimed.

See [final measurements](final-result.json), [execution result](execution-result.json),
[oracle](oracle.json), [diagnosis](diagnosis.json) and
[runtime input measurements](runtime-input-measurements.json). The
[cohort report](cohort-report.json) retains this failed slot; the other five generic
cohort slots were not requested for this follow-up and remain unattempted. No
execution was replayed, guard weakened, observation truncated, budget increased or
artifact rewritten. A future correction requires a separate unapproved draft and
fresh review under the existing rules.

## Cumulative accounting and limits

| Campaign accounting, EUR | Before planning | After planning | After execution |
| --- | ---: | ---: | ---: |
| Known estimated cost | 137.041241 | 137.300060 | 137.303548 |
| Unconfirmed reservations | 19.877944 | 19.877944 | 19.877944 |
| Combined upper bound | 156.919185 | 157.178004 | 157.181492 |
| Authorized ceiling | 200 | 200 | 200 |

Approximately EUR 42.82 remains under the retained upper bound. These are usage
estimates and conservative reservations, not invoices. Historical unknown usage
remains unknown. This failed single-case execution cannot support a comparative
business-correctness, safety-equivalence or cost-reduction claim. The broader
baseline/candidate comparison remains inconclusive.
