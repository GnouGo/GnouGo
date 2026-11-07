# Fresh authorized live allocation — 7 November 2026

The operator explicitly requested: “reset to 0 ceiling and restart with 400 euros authorized. live tests.” This authorizes **€400 of independent new spending starting at €0 new consumption**, not erasure or reconciliation of prior liabilities. The old campaign and every old request/reservation are retained. Global available funding remains unknown.

Implementation source: `202048374d4d070f7f2f4c623eceac35a66b9997` on PR #120's current branch. Only the existing benchmark/operator harness changed; production financial admission, compiler, judges, permissions and replay contracts remain unchanged. User-staged solution and `.vscode` changes are preserved.

## Authorization and configuration

- New campaign: `pr120-live-20261107-400`; mode **Limited**, ceiling **€400**.
- One-use grant: `gnougo-pr120-user-20261107-fresh-400-v1`.
- Exact deployment: `OpenAi / gpt-6.1-sol-2026-09-29`, configured in KeyVault. No provider, endpoint, protocol or model substitution.
- Current immutable configuration: `9ac37743a2b60c1b666d140cc45e95cfdbfb7a0f72c897a668ed5e9a2f01d34b`. All five selection guards pass, with no differences from the current deployment.
- New authenticated sessions capture diagnostic warning authority and configured phase allocations (navigation/draft 8,192; review 2,048 output tokens, medium reasoning); maximum-model allocation remains disabled. Experimental future forecasts warn; current requests/actual settlement, uncertain attempts, pricing and finite retries/timeouts remain mandatory.
- `--retained-external-reservations-eur 0` refers only to liabilities outside HTTP journals **within this newly authorized allocation**, initially zero. It does not erase the predecessor's external holds or assert that they were paid.

The predecessor `schema-portability-20261019-incremental` still has its exact ceiling `740.0301038327063045424449473`, known captured-tariff estimates `475.28093332377412455040481684`, and pending campaign reservations `222.7236032944974286315167358` EUR. External provisional holds remain unreconciled. See the unchanged [previous financial investigation](../financial-admission-2026-11-06/README.md) and this directory's fresh predecessor audit. These are not provider invoices.

## Validation

- Focused funding/idempotency/interruption/configuration/receipt regressions: **151 passed, 0 failed, 0 skipped**.
- Wider Server financial admission/settings/default resolution tests: **189 passed, 0 failed, 0 skipped**, including those 151; do not add the counts.
- Existing encrypted KeyVault Core suite: **19 passed, 0 failed, 0 skipped**. The new allocation also survives actual process recreation from probe to generic workflow to Amazon, using the same encrypted records.
- Frontend production build: passed (no UI source change).
- Release benchmark and complete solution builds: **0 warnings, 0 errors**. Source-bound compressed logs/TRX files are retained here.
- Previous production freeze `77dfe367`: 7,982 passed, 0 failed, 13 platform/opt-in skips, with separately documented production frontend, Native AOT and published encrypted-storage qualification. Those results are baseline evidence, not reruns on the new harness commit. This change adds no production serialization, package or UI code.

## Actual provider evidence

The protected actual Agent.Server frontend passed asset/helper/interactive-circuit readiness. The small nonce-based structured-output probe used the planner's same configured client, endpoint and foreground protocol. It passed with **one physical HTTP attempt and a durable receipt**: 96 input tokens, 31 output tokens, no uncertain attempts, and a captured-tariff estimate of `0.0075772503358710255261979400` EUR. See `probe.json` for exact request/trace identities, arguments, loaded assembly hashes and source.

Workflow trials and final funding reconciliation are recorded below after completion. Generation qualification stops at final review; approval and execution are separate.

### Generic actual-provider workflow

Session `01b2af8bf8164c6f8ead9447d1df1d98`, run `fresh400-local-20261107-01`, used the unchanged deterministic “6 times 7” request. The actual protected Server path reached **final review**, revision 12, after **5 logical calls**, zero repairs and no blocking diagnostics. Discovery was correctly skipped because no external capability was needed. All five calls have receipts; measured usage was 8,744 input and 641 output tokens (reasoning is included). Captured-tariff estimate: `0.338289296909986565158978952` EUR. Execution was not started; this is not an execution/oracle claim.

The real browser observed the active phase, opened the common inspector, found five unique logical request identities and reported zero browser errors. `local/live-common-inspector.png` also shows an advisory input-target warning permitting continuation. Protected redacted exchanges remain in existing encrypted journals and local ignored artifacts; only metadata/screenshots are committed. The run's read-only finance projection confirms Limited €400, complete new-session pricing evidence and zero unresolved attempts. It does not establish global available funds or provider invoices.

Probe plus generic workflow: six physical attempts, `0.345866547245857590685176892` EUR known tariff estimate and zero reservations at the captured post-generic boundary. The immutable predecessor accounting remains unchanged.

### Amazon actual-provider trial and new interruption

Run `fresh400-amazon-20261107-01`, session `d5f12d9d59f54bda9fefe441d19c0834`, used the unchanged Amazon scenario and exact deployment. It passed the previous configuration/financial/startup blockage, received **eight successful provider responses**, completed initial discovery metadata transitions, and reached drafting. The authoritative encrypted session store retains revision **21**, `construction_draft`, with pending request **9** and no accepted checkpoint yet. The last harness observation was revision 20 / eight calls; that polling snapshot predates the ninth request and is explicitly retained separately. No approval, workflow execution or independent execution-oracle success is claimed.

The first new exception was `KeyVaultAccessException` wrapping **SQLite Error 5: database is locked**, in `KeyVaultRecordStore.WriteAuditAsync` called by `GetCoreAsync` while Server's cancellation observer read a session snapshot. It caused a background-service failure and host shutdown. This is a local storage failure, not an LLM HTTP refusal or `DISCOVERY_INCOMPLETE`. Full safe stack evidence is retained in `amazon/host-interruption.log.gz`.

The harness admission still read/decrypted the **entire tenant's** HTTP/request history before filtering to one campaign. With paid inference stopped, the public-record measurement read **7,110 records / 50,186,411 logical bytes in 6,925.7319 ms**. Targeting this campaign read **15 records / 320,935 bytes in 29.4727 ms**. The original long snapshot exceeds the existing 5,000 ms busy timeout. This demonstrates a concrete contention contributor; it does not identify every concurrent lock holder or establish universal lock immunity.

`729ca723` replaces admission/accounting/policy/retention scans with the existing bounded metadata capability and exact owned-record reads. Metadata-only request-presence checks avoid hydrating request contents. The old store API fallback remains supported. Tests cover >200 owned records, unrelated history, missing payloads, invalid cursors and foreign ownership. **193 passed** after this correction. Fresh predecessor accounting compared exactly equal before/after, including the ceiling, tokens, known costs and reservations; no historical records were repriced or cleared.

The original request 9 has no receipt. Both its Server primary HTTP journal and campaign HTTP journal contain **zero attempts**, and the retained cancellation failure is `TaskCanceledException` at dispatch stage. Those facts alone do not authorize replay. Its **`2.4215763546798029556650246305` EUR application reservation remains retained**, separately from the campaign's zero HTTP hold. No reservation was released or counted twice.

`c0fc6866` now checks unresolved campaign requests **before** a fresh live host/session/reservation is created. **194 affected tests passed**, and both the complete solution and independent Release harness build without warnings. A fresh invocation of the actual Amazon launcher (`fresh400-amazon-20261107-02`) refused with `LLM_BUDGET_UNVERIFIABLE / campaign_uncertain_request_requires_recovery` before creating a host or session. That is a replay-safety preflight, not another paid trial. The narrowed admission path has therefore not yet been qualified by another paid Amazon continuation; exact recovery or eligible permanent retention of request 9 is the first remaining obstacle. No new recovery mechanism or unsafe bypass was added.

### Final new-allocation accounting

- **14 completed real HTTP exchanges / receipts**: probe 1, generic workflow 5, Amazon 8. The pending ninth Amazon request is separate; a reservation is not proof of HTTP dispatch.
- Measured totals: **34,533 input tokens and 3,085 output tokens**, with reasoning included in output totals.
- Known captured-tariff estimates: **`1.425248544558889386475593372` EUR**; these are not provider invoices.
- Retained application hold not represented by campaign HTTP accounting: **`2.4215763546798029556650246305` EUR**.
- Remaining **new authorized allocation**, after those estimates and holds: **`396.1531751007613076578593819975` EUR** (conservative display: **€396.15**).
- Global funding and old unpriced liabilities remain unreconciled/unavailable. The predecessor ceiling and all old expenses/reservations remain unchanged. No Unlimited mode, grant reuse, second grant, monetary reset, manual plan edit or uncertain replay occurred.

See `funding-final.json`, `campaign-audit-final.json`, `predecessor-preservation.json` and the protected common-inspector evidence. Full redacted exchange artifacts remain locally under the existing ignored live-server artifacts and encrypted journals; no new call viewer, journal, provider fallback or storage service was introduced.
