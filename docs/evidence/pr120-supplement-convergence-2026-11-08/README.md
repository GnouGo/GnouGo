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
