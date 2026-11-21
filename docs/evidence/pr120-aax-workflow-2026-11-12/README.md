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

## Actual-profile source investigation

Two [fresh source-only checks](source-checks.json) on frozen `e4a3288e` retain every
record, complete ordered reads, cleanup and identical cold verification. Each
succeeds on four legacy source resolutions and then fails at the first AAX read:
the owning Browser has no current exact-entry grant. No model or business visit
is dispatched. The first search changes only query spelling (`%20` to `+`) and
has two capture navigation invalidations. A second independent check explicitly
GETs the exact observed document address, without rewriting/normalizing it. Its
address remains unchanged but a capture navigation invalidation and the same
refusal remain. The address-spelling hypothesis therefore does not resolve it.

The retained complete snapshot passes a standalone production-scope publication
and literal validation test, with no navigation. This separates valid retained
content from actual live authority. Prior logs cannot distinguish stale-generation
publication from later source expiry. Add only useful lifecycle diagnostics to
the existing Browser logger/activity: reason, generation, expected generation,
entry count and opaque capture ID, never URLs, patterns or record contents.
The controlled race regression verifies withheld authority, a later valid fresh
publication, observable reasons and redaction. All 215 Browser tests pass in
Release with `-warnaserror`, zero skips. Navigation and expiry rules are unchanged.
Another fresh source-only check must identify the actual transition before any
behavioral correction or paid full planning.

## Demonstrated owned-response timing defect

The [fresh lifecycle trace](lifecycle-diagnostic.json) shows the source grant was
withheld before any dependent read: a commit during complete acquisition was
classified `unowned_source_commit`, changing generation 1 to 2. It was not later
expiry after a published grant. A deterministic Kestrel regression starts a second
same-address GET while the original navigation read owns it, holds its response,
then commits it after the navigation helper returns but before complete acquisition
finishes. The retained source is complete, but the next exact observed entry is
denied. The semantic red fails at that entry in 736 ms; an earlier fixture setup
that deadlocked during readiness is retained separately and is not this semantic red.

The generic correction retains the existing navigation read's exact response
index and source generation until complete acquisition ends. A pending request
already in that index keeps its own ownership after the helper returns; URL
equality alone establishes nothing. An unowned new source request, including one
to the same address, still changes generation and cannot borrow that ownership.
The reference is cleared when acquisition finishes. No URL, route, source query,
permissions, timeouts, retry count or business classification is changed.

All **217 Browser tests** and **557 affected Amazon/delegated-review tests** pass,
Release, `--no-restore -m:1 -warnaserror`, zero skips. The new test seam is internal
and disabled outside controlled tests; public tools and persisted formats are
unchanged. Private logs are `source-late-commit-semantic-red.log`,
`source-owned-commit-final-browser.log` and `source-owned-commit-affected.log` under
the existing artifact directory. A fresh actual-profile preflight must validate
this correction; these controlled results do not qualify live business execution.
