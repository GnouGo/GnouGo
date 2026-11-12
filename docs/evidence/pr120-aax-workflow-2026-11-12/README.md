# Approved AAX v10/v14 workflow integration

The operator explicitly approved the exact proposal at `f207d8b5`, including its
two disabled-by-default trusted Browser configuration fields. The generic owning
Browser capability is committed separately in `fba979a7`.

This stage adds source policy `amazon-observed-aax-product-context-v10`, business
verifier `amazon-business-v14` and the exact grant
`user-amazon-aax-workflow-20261112`. Older policies/verifiers and the inactive
relationship report retain their behavior. No campaign grant, provider, model,
transport, retry, allocation, timeout or stopped session is changed.

The disposable MCP profile keeps the old static host/routes and configures the
two trusted observed-navigation lists. AAX is never a broad redirect wildcard.
The independent verifier binds every AAX hop to an exact original source href,
checks literal redirect spelling before canonicalization, and requires each
unresolved occurrence's own ordered proof and distinct capture. Legacy and AAX
resolutions remain in their combined original order. Empty-ASIN context analysis
reuses the existing inactive prototype's relationship implementation; missing,
mixed, conflicting or unproved members remain failures. Review aliases claim no
HTTP receipt. Source reads do not replace later ordered business visits, observed
fields, actual workbook cells, execution success or cleanup.

## Controlled validation

After restoring missing pinned NuGet packages without changing versions, a
behavioral baseline routes the new fixture through preserved v13 classification:
**2 fail, 15 pass**. The new source implementation passes all 17 original cases.
The final expanded suite passes **557 affected Amazon and delegated-review tests**,
Release, `--no-restore -m:1 -warnaserror`, zero skips. This includes the inactive
prototype, old version negatives, source-order/repetition/expiry, literal redirect
rejection, fresh capture requirements, profile/grant isolation and complete
workbook-oracle negatives. One positive workbook regression initially exposed a
missing v14 source-read exclusion; the existing exclusion now includes only the
new version after complete independent source qualification. It does not remove
raw records or any business visit.

Private logs: `artifacts/pr120-approved-20261112/aax-workflow-semantic-red.log`,
`aax-workflow-green.log`, `aax-affected.log` and `aax-final-affected.log`.
The failed intermediate run is retained as failed; it is not a qualification pass.

Actual-profile source readiness and cold verification must pass before a fresh
paid Amazon plan. Controlled fixtures establish neither a live accepted plan nor
real business execution or independent qualification. Amazon and Code remain 0/2.
