# Authorized store-context workflow amendment

The user explicitly replied **“Authorize this exact workflow amendment”** to the
proposal in commit `f0a08ed5`. This activates the tested store-destination proof
only for fresh runs capturing `amazon-observed-resolved-store-context-v4`, grant
`user-amazon-workflow-store-context-20261111`, and `amazon-business-v8`.
It adds no money and does not upgrade historical sessions or v7.

## Cause and bounded correction

The earlier real source contained 17 empty-ASIN sponsored links. All 17 actually
resolved to approved store pages. V7 lacked that independent evidence alternative;
no missing ASIN was recovered or invented. V8 reuses the existing context checks
with exact ordered resolution evidence. HTTP and document destinations must both
be approved store pages. Heading/occurrence/product conflicts remain blockers.
The only added redirect route is the already approved Amazon.fr
`/stores/page/{UUID}` with optional query. Direct store entry is forbidden.

The execution guard permits only the next exact unresolved source occurrence,
then the original product links once every context qualifies. The new regression
also demonstrated that rejected contradictory browser provenance retained the
previous entry set. The scoped correction clears that authority before throwing;
its regression was red before the fix and green afterward. Close, failure and
new-search expiry remain enforced. No production engine or TaskPlan change.

## Controlled validation, not workflow qualification

All **328 affected tests pass** with `-warnaserror`. Coverage preserves v7 and
checks incomplete/contradictory/expired source evidence, exact permissions,
repeated product identities, missing/reordered product visits, workbook contents,
observed fields, writer receipt, execution status and browser cleanup. The full
oracle positive case uses controlled events and a locally generated XLSX: it is
not a real Amazon execution.

A cold process applied v8 to the retained real source-only events: source
prerequisite qualified, 17 resolutions, zero product visits; business selection
correctly fails `first_product_visits_incomplete`. These historical events confer
no authority on a new browser or workflow.

The [fresh prerequisite runner](source-preflight/Program.cs) uses the real KeyVault
host configuration and the execution tool guard. It creates no artifact review,
model request or workflow execution approval. A fresh actual-profile prerequisite
must pass before full Amazon planning. Subsequent real results are recorded
separately; this implementation does not claim an accepted plan or oracle success.

## Financial and external boundaries

The opening campaign audit retains ceiling **€1,700**, known usage tariff estimate
**€816.4876578593819973130318079**, unresolved HTTP holds
**€407.92750559785042543663233336**, and retained application reservations
**€59.555978487450312513390730382**. Remaining campaign allocation is
**€416.028858055317264736945128318**; provider invoices/global funding are unknown.
No supplement, reset, released liability or replay is authorized by this amendment.

Code remains blocked on unavailable route-owner access. The prepared packet stays
unsent. A local silent-stream test is not a test through that inaccessible route.
