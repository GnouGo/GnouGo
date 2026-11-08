# Existing-campaign supplement and targeted convergence

The explicit authorization `pr120-supplement-20261108-400-01` increases only
campaign `pr120-live-20261107-400` from EUR 400 to EUR 800. The existing leased
`extend-budget` operation was invoked once after checking that this reference
was absent. [The verified ledger](supplement-once.json) contains exactly one
matching entry. The original grant, 601 physical attempts, known charges, all
22 unknown attempts and every reservation are unchanged. Immediately after the
operation, EUR 402.9363636363636363636363616195 was available after HTTP holds
and the separate external reservation. This is an allocation balance, not
provider invoicing or established global funding.

The first regression reproduced an existing incompatibility: fresh-allocation
admission required the current ceiling to equal the original grant. The generic
correction accepts only the existing continuous supplement-authorization chain,
with a unique reference and exact previous/next ceiling for each entry. It keeps
the immutable original allowance and reports the current authorized ceiling
separately for new Server sessions. Restart/idempotency, retained uncertainty,
admission above the old ceiling, hard refusal above the new ceiling and malformed
chains are covered. All **183 focused campaign tests passed** without warnings.

The [baseline planner CI run](https://github.com/GnouGo/GnouGo/actions/runs/42482140139)
failed five interactive browser tests: it skipped the frontend build and never
produced the required helper assets. The CI correction installs the pinned pnpm
and Node versions, installs the frozen workspace lockfile with strict peers,
then builds Agent.Server ClientApp before browser tests. Shared UI sources and
root package/lockfile changes now trigger that workflow. The actual production
frontend build and all **eight focused startup/diagnostic/clarification browser
tests passed locally** afterward, including delayed and missing-asset cases.
This local verification does not claim a subsequent remote CI pass.

The targeted live trial uses a fresh real code-review workflow. Its exact
unchanged-plan business-review transition must be observed before claiming the
`bcc0434f` boundary exercised; successful arithmetic is not evidence of it.
Cancelled historical sessions are retained and cannot resume.

The first targeted trial on `868a2868` stopped at discovery call 14 with no
accepted contribution. [Its original streams and exact-request comparison](boundary01-and-discovery-guide-comparison.json)
show two native transport endings about 300 seconds after their last bytes,
before both captured 600-second deadlines. The existing owner admitted one
independent attempt, retained both full uncertain holds and stopped at its
allowance. Exact permanent retention preserves all originals and forbids old
continuation/replay. This run does **not** exercise the business-review boundary.

An independent protected Server probe changed only HTTP message content, adding
a **724-byte compact required-field guide**. Every other wire field, strict
response grammar and original context remained identical. It returned a valid
response in one physical attempt, 16.35 seconds through its saved report, at
EUR 0.2479265562024182713837886252 in captured-usage tariff estimates. No candidate
or business tool was applied. This supports testing field visibility, not a claim
of causality, an upstream diagnosis or time/cost to an accepted contribution.

Fresh discovery requests use this guide instead of a full prompt copy of the
grammar. Diagnostic captures retain it even when an advisory input hint is crossed.
Ordinary Strict hosts omit only optional guidance when the whole request would
cross their existing maximum; all context, contracts and hard admission stay intact.
This boundary corrected a newly reproduced strict follow-up regression, without
raising a limit. **169 focused planning tests passed**, including discovery,
retained recovery and business-block completion. Final solution validation remains
required after live work stabilizes.

The generic Browser correction passes **157 component tests**, including controlled
ARIA/native occurrences with multiple links, duplicate destinations, page order,
actual final-document/response provenance, cookies, forbidden host/path redirects,
loops, the physical redirect ceiling and non-replayed POST interactions. The
component's Release publish also passed. No Amazon permission or oracle was
changed. See the [separate scope proposal](amazon-scope-proposal.md); it grants no
authority and cannot be used as a successful Amazon qualification.
