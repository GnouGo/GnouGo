# Proposed exact-entry AAX workflow amendment

Status: **inactive; separate approval required**. No workflow policy, host profile
or business verifier activates AAX. No additional budget is requested.

## Completed authorized investigation

On frozen `8590c871`, the source-only diagnostic captures 1,581 records through
the configured Release MCP. Its three literal AAX occurrences include a repeated
heading link. Each receives its own ordered GET 302 → GET 200 chain and a distinct
capture; all reach product identity `B0H8RD31XM`. The original MCP source remains
open during the guarded production-Browser resolution reads, which use a distinct
browser context. Cold verification and both browsers' cleanup pass. No model
call, business visit, product extraction, workbook or full workflow occurs.

The [source-only evidence](source-diagnostic.json) preserves the scope boundary.
An inactive relationship prototype requires every navigable member's own ordered
product proof, consistent original empty-ASIN context, exactly one heading, one
matching product identity and only exact same-card review twins. Aliases claim no
HTTP receipt. Missing/reused/out-of-order proof, unobserved redirects, conflicting
identity, missing twins and changed source provenance fail. The actual retained
source is [analysed separately](actual-relationship-analysis.json); this never
qualifies its other unresolved links.

The [controlled validation](validation.json) passes 36 source/prototype checks and
520 affected Amazon/review tests in Release with `-warnaserror`, zero skips.
Those overlapping counts are not added together. The prototype performs no
navigation or model call and is not registered as a workflow verifier.

The earlier long-literal matcher defect is fixed and validated independently in
[`8590c871`](../pr120-literal-navigation-2026-11-12/README.md). No regex limit,
URL spelling or navigation permission was changed by that correction.

## Proposed implementation and authority

Implement and explicitly activate a new **v10 source policy / v14 business
verifier**, preserving v9/v13 and all earlier versions:

- Authorize initial `browser_get_content` GETs only to exact fragment-free
  `https://aax-eu-zaz.amazon.fr/x/c/...` hrefs observed in a complete successful
  current Amazon.fr search. Source-proof reads retain only the unresolved
  occurrences, their order and repetitions. Later first-N business visits remain
  mandatory and separate, including when they use an AAX entry.
- Before contacting each redirect destination, allow only the existing approved
  Amazon.fr routes or an exact AAX entry in that same observed source. A broad AAX
  redirect-prefix grant is **not** proposed. No decoded target, normalized URL,
  fragment navigation, other host/path/port/credentials or unobserved AAX target
  gains permission.
- Reuse the owning Browser's complete snapshot and navigation lifecycle for a
  generic observed-link authorization scope. Only explicit content reads gain
  initial entry authority; other interaction tools retain their original rules.
  Clear authority on close/disconnect, a new matching source navigation, or failed
  evidence. A failed or incomplete source cannot populate it. Retain the original
  complete observations; permissions create no product identity or completion.
- Enable that generic Browser capability only through two new optional **trusted
  host configuration fields**: `ObservedNavigationSourceUrlPatterns` and
  `ObservedNavigationEntryUrlPatterns`. Both default to disabled and must be
  validated together. For this profile they describe the existing Amazon.fr search
  route and the strict AAX entry route. Existing base routes remain unchanged;
  AAX is admitted by exact observed membership, not a static route wildcard.
  Configuration and approval are captured through the existing run/profile hashes.
  Model inputs cannot set either field or grant permission.
- Extend only the separately versioned independent proof rules for these source
  entries and demonstrated product-card relationships. Preserve every raw record,
  unresolved context, repeated occurrence, product order, own redirect proof,
  later business visit, requested field, real Excel row and cleanup requirement.
  Unsupported, mixed or inconsistent cards remain failures. No source proof is a
  later business visit or an executed workbook result.

Keep the single TaskPlan, typed fragments, deterministic compiler, scoped repair,
business reviews, inspector and financial journals. Do not change provider,
endpoint, model, transport, timeouts, retries, output allocations or funding.
Source lifecycle/tenant negatives and existing verifier negatives must pass;
then validate the actual configured MCP profile before any paid full Amazon run.
Two complete independent executions on one frozen version remain required.

## Why another approval is necessary

The [approved source-only proposal](../pr120-amazon-unclassified-context-2026-11-11/proposal.md)
explicitly says it “does not authorize new product classification, a workflow
policy/oracle version”. This proposal activates those capabilities. It also needs
an explicit, narrow exception to the earlier **“No public API, persisted-schema
or protocol-version change”** boundary for the two optional trusted configuration
fields. Their production API/configuration surface is **not implemented yet**.
Public MCP tool arguments, TaskPlan/persisted planning formats and protocol
versions would stay unchanged. The currently implemented prototype is internal,
offline and grants no authority.

The existing source-only grant remains usable independently. Declining or deferring
this amendment leaves AAX full workflows blocked; it does not release funds or
authorize a different route.
