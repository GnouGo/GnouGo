# Durable inference admission

The retained `canonical20261007b-amazon-1` execution stopped before runtime provider dispatch: campaign currency conversion was unavailable, and the ordinary exception left no terminal mapping receipt. Its invocation, reservations, artifacts and failed execution evidence remain unchanged. This correction does not reconcile or replay it.

## Accounting readiness and admitted quotes

The existing live harness now checks exact deployment allowances, model pricing and currency conversion before starting external work. Quotes retain their authority, date and currency pair in encrypted campaign records. Fresh requests may reuse them only under the existing seven-day freshness policy. Missing, stale, invalid or unavailable quotes remain explicit blockers.

A request stores its validated quote atomically in the existing HTTP journal before dispatch. Settlement uses that quote, including after restart, without another exchange-rate request. Expiration prevents new admissions; it does not retroactively change a completed request's conversion. Existing monetary ceilings and conservative reservations are unchanged. Historical admissions lacking a stored quote are retained for explicit recovery rather than reconstructed using today's rate; completed historical receipts remain readable.

## Refusal versus unknown completion

Known pricing/quote failures before dispatch use `LLM_BUDGET_UNVERIFIABLE`, `dispatch_status: not_started`, and a bounded reason. The campaign verifies that no prior request or physical attempt could have dispatched and durably binds the refusal to the original request hash. Repeating that identity returns the same refusal without inference; changed payloads are rejected.

Flow reuses its existing completion-receipt path, allowing ordinary failure cleanup after the refusal is durable. Mapping budget failures now require the same explicit non-dispatch disposition as `llm.call`; a budget error after dispatch cannot establish completion by itself. Error details survive receipt replay. Extraction, sandboxing, mapping cache identity, attempt limits and historical receipt shapes are unchanged.

Failed receipt publication and unverified post-dispatch failures still stop for reconciliation and prohibit cleanup/replay. Historical failures without the new verified disposition are not upgraded. Campaign failure evidence is mandatory before a verified refusal is exposed; optional diagnostic writes for already-uncertain failures retain their existing behavior.

## Deterministic validation and rollout

The new regressions use injected pricing/rate/provider adapters, actual encrypted KeyVault records and real Flow execution. They cover pre-execution blocking, quote expiry and restart, settlement without network access, both inference paths, cancellation, request identity, receipt-write failures, tenant/campaign isolation and retained unknown reservations. No paid calls or campaign migrations are required.

Run the focused gate with:

```bash
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true \
  --filter 'FullyQualifiedName~BenchmarkAdmissionTests|FullyQualifiedName~BenchmarkCampaignTests|FullyQualifiedName~BenchmarkMetadataTests'
```

Use the updated harness for future runs. Read-only inspection remains available when currency readiness fails. A new live evaluation still requires separate authorization, fresh identities, concrete artifact review and the existing campaign budget gate. This deterministic correction does not establish live execution success.
