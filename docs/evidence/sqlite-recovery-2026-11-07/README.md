# SQLite recovery and funded Amazon continuation — 7 November 2026

This continues the [fresh €400 campaign](../fresh-live-allocation-2026-11-07/README.md) on PR #120. The campaign remains `pr120-live-20261107-400`, Limited €400, with its original one-use grant and immutable deployment configuration `9ac37743a2b60c1b666d140cc45e95cfdbfb7a0f72c897a668ed5e9a2f01d34b`. No supplement, reset, Unlimited mode or grant reuse occurs. Predecessor liabilities remain unreconciled and global available funding remains unavailable.

## Storage correction

`d1dda10b` removes cryptography from normal SQLite snapshot/write-transaction lifetimes. Full reads copy encrypted manifests/chunks in one consistent snapshot, commit, write the required audit, then decrypt/authenticate. Normal range inspection releases its read snapshot before audit/decryption. Writes encrypt chunks before reserving the writer, then verify the exact record identity inside atomic publication. Historical conversion retains its exclusive recheck. Targeted campaign reads from `729ca723` remain in place.

BUSY/LOCKED errors retry the rolled-back storage operation at most five times, with a one-second provider wait per attempt and cancellable 50/100/200/400 ms delays. Other failures are not retried. Exhaustion remains an explicit storage exception; audit is mandatory. Cancellation surveillance reads only the small encrypted cancellation marker. Recovery selects bounded active index coordinates and cancellation metadata. Storage failure suspends the affected owner without terminating the host or rewriting its retained work as failed. Exact journals, ownership and replay guards remain authoritative.

## Original Amazon request 9

Session `d5f12d9d59f54bda9fefe441d19c0834` remains at revision 21 with the original pending request 9. Inspection first checked both receipt locations and both HTTP journals. No recoverable response exists. The Server journal has one reserved attempt and no transport outcome; the campaign has an empty attempt list and a retained `TaskCanceledException`. These leave dispatch **unknown** and do not prove non-dispatch.

`9d3a0089` extends the existing `interrupted-process` permanent-retention preview/apply flow to this offline Server state. It holds the existing session owner lease, reads the authoritative revision, verifies exact request/configuration/policy/index/hold identities, and rejects active ownership, receipts, transport outcomes, tool use and approval/execution markers. The generation request is tool-free and no business artifact was approved or executed.

The operator-authorized retention was applied against evidence hash `cbbd63986f2a6b69a321666a7a96baaa3d05a814587344c00df04f3c0f3e5ace`. Its outcome is **inconclusive**; request replay and run continuation are permanently forbidden. The complete **€2.4215763546798029556650246305** application reservation remains outside campaign HTTP accounting and is enforced at every new physical admission. It is neither a success nor a zero-cost outcome. Public-record rereads compared all nine original session/request/HTTP/failure/policy/budget records equal before and after retention. No receipt was manufactured.

`422836b8` shares the same attributable-hold check with runtime inference. Exact saved receipts/responses remain recoverable even if current funding evidence changes. Additional unverified external history still refuses new dispatch. The hold is counted once and never settled by administrative retention.

See [exact retention preview](retention-final-preview.json), [applied retention](request9-retention.json), [unchanged original records](request9-record-integrity.json) and [initial accounting](audit-before.json).

## Real-provider trials

All trials use the actual protected Agent.Server path, the configured `OpenAi / gpt-6.1-sol-2026-09-29` provider, source-bound Release tools, diagnostic warnings, the same campaign and the preserved external hold. The ordinary shared inspector exports the redacted exchanges. Original requests/plans are not manually edited.

| Trial | Session | Source | Allocation | Result |
| --- | --- | --- | --- | --- |
| `storagefix-amazon-20261107-01` | `4aad13882bbb4232b92bc905b7d30772` | `9d3a0089` | Configured 8K draft | 10 real HTTP exchanges/receipts: 9 complete responses, then an 8,192-token reasoning-only output limit. No accepted plan/execution. |
| `storagefix-amazon-20261107-02` | `72a367d537d74885aca51d074ac3e293` | `9d3a0089` | Existing 16K reasoning profile | 11 real HTTP exchanges/receipts: 10 complete responses, then a 16,384-token reasoning-only output limit. No accepted plan/execution. |
| `storagefix-amazon-20261107-03` | `ed724bc7829b46988851371be967a9e7` | `422836b8` | Existing 32K complex profile | 10 completed responses, then one uncertain transport outcome after 300 seconds. No accepted plan/execution. |
| `storagefix-amazon-20261107-04` | `0eb17725cede426bbeddde86cc51081b` | `422836b8` | Existing 32K complex profile | 16 completed responses; request 17 exhausted three captured attempts, each receiving HTTP 500 / `ESG120` after about 300 seconds. No accepted plan/execution. |

The first trial passed the previous ninth-call interruption and dispatched call 10 without a storage incident. Both completed trials retained all usage and their output-limit responses. The browser opened the common inspector, observed active/final states, found no duplicate logical identities and reported no browser errors. Their received output limits do not authorize replay; each subsequent trial uses a fresh independent session with its own captured allocation.

After the first two trials, campaign accounting has 35 completed physical exchanges, no new uncertain HTTP attempts, and **€9.551285266457680250783699065** known captured-tariff estimates. After the unchanged original hold, the remaining new allocation at that boundary is **€388.0271383788625167935512763045**. These are tariff estimates and reservations, not provider invoices. See [first-trial accounting](audit-after-amazon01.json) and [second-trial accounting](audit-after-amazon02.json).

The third trial retained one physical transport attempt with no status/body/usage.
Its 300-second connection failure is distinct from the captured 600-second attempt
bound. The host remained healthy. Both receipt locations and HTTP journals were
inspected; no exact recovery is possible from their retained evidence. On explicit
additional user authorization, the existing `exhausted-planning-retries` retention
flow permanently retained this request as inconclusive against evidence hash
`316c411284a6c54ad60dc616b02898e32d65a86373d01b36154728381f19d04e`.
The failure's `retry_exhausted: false` remains unchanged: uncertainty, rather than
three confirmed transient errors, forbids retry. The complete campaign hold is
**€7.6513569189431258396775638153**. It already covers the corresponding application
hold, so that application hold is not deducted again. Campaign accounting compared
exactly equal before/after retention. Old replay/continuation are forbidden; the
fourth trial is independent. See [third-trial result](amazon-03/result.json),
[retention preview](amazon03-retention-preview.json), [applied retention](amazon03-retention.json)
and [accounting](audit-after-amazon03.json).

The fourth trial reached an independent implementation reselection and further
discovery/drafting. Its final request retained all three error-only response bodies;
the existing captured transient-error policy allowed those attempts and then stopped.
Their recorded error says `Routing failed` and `Backend not available, no answer from backend.`
There is no successful generation or usage receipt to recover from those HTTP 500
envelopes. No uncertain transport was replayed. The complete reservation for each
attempt remains; the newest request is still pending reconciliation and was not
permanently abandoned. This repeated backend failure is the next actual blocker,
distinct from the corrected SQLite crash and from a read-only replay preflight.
See [fourth-trial result](amazon-04/result.json) and [final campaign accounting](audit-after-amazon04.json).

Across the four new trials there are **51 new physical HTTP attempts**: **47 durable
model responses**, including two output-limit responses; one missing transport
outcome; and three fully received HTTP 500 error envelopes without usage. Of the 47
model responses, **45 completed normally**. None of these facts establishes an
accepted plan. Accepted checkpoints, delegated artifact approvals, workflow
executions and independent Amazon business-oracle successes are all **zero**.

The final captured campaign boundary has 65 physical attempts, **€14.504711150918047469771607717**
known captured-tariff estimates and **€29.289888042991491267353336319** campaign HTTP
reservations, plus the original **€2.4215763546798029556650246305** hold outside HTTP
accounting. Remaining new allocation is **€353.7838244514106583072100313335**
(conservative display **€353.78**). The four unresolved HTTP attempts retain uncertain
usage: one transport failure and three received error responses. The original
request-9 application hold is separate and counted once. Provider invoices and
globally available funding remain unavailable. A final [public-API accounting reread](audit-final-reread.json)
compared exactly equal to the fourth-trial boundary. The [funding summary](funding-final.json)
keeps all reservations and distinguishes campaign allocation from unavailable global funding.

## Validation

- KeyVault Core: **23 passed**, including real contention across independent stores, a blocked audit held beyond one attempt, bounded exhaustion, cancellation and cold data recovery.
- Focused Server ownership/campaign/retention: **324 passed** on `9d3a0089`. Watcher failures preserve late receipts; cold service recreation recovers once or applies a retained cancellation without duplicate dispatch/accounting.
- Runtime/retention/admission: **72 passed** on `422836b8`, including attributable holds, rejection of unverified extra holds and exact already-paid receipt recovery without another dispatch/charge. Counts overlap; do not add them.
- Latest combined Server selection: **386 passed**, with zero failures/skips/compiler diagnostics. The later **7 ownership tests** on `52a6b596` also pass, including a real SQLite write reservation shared by financial admission, session save, inspection and cancellation, plus an actual two-process late-receipt continuation with zero second-process dispatches, unchanged receipt hash and one 15-token charge. These overlap the combined selection; the separate-process test was added afterward.
- Release benchmark builds, KeyVault Release pack and the published trimmed/self-contained Agent.Server encrypted-storage/exact-receipt smoke passed without compiler warnings.
- Full solution validation on shared production source `9d3a0089`: **8,005 passed, 5 failed, 13 skipped**, across 33 test assemblies; zero compiler diagnostics. The failures all came from the minimal clarification HTTP fixtures omitting dependencies required by the current complete endpoint map. Materializing that map reproduced the exact ASP.NET inferred-body error before serving HTTP. `af54a94a` registers those fixture dependencies and validates the map before startup: **all 10 clarification tests pass**, including the five failures, with zero failures/skips/compiler diagnostics. No production route or permission changed. The entire solution was not rerun after this fixture-only correction.
- The initial solution build stopped on a GitHub download timeout. The completed rerun used the exact pinned Copilot CLI 1.0.88 archive after SHA256 verification (`49f5caa0582945a04c5619f8ea81680b1c8c2765888290cf8acf903e2985315b`); no dependency version changed. The 13 skips are seven Windows-only cases, one opt-in clarification browser case and five opt-in live Copilot cases; the actual Amazon browser/provider trials above did run.

Exact commands, source attribution, counters and skip/failure names are in [validation.json](validation.json).
The [full solution log](full-solution.log.gz), [33 TRX reports](full-solution-results.tar.gz),
[clarification reproduction](clarification-endpoint-reproduction.log.gz) and
[passing clarification rerun](clarification-tests.log.gz) preserve the original outcomes.
Supplementary focused logs and TRX files are in this directory; their counts overlap.

The SQLite interruption is corrected and real-provider continuation is demonstrated.
The requested Amazon business outcome remains incomplete: the configured backend must
return a usable generation before artifact review, execution and the unchanged
independent oracle can proceed. The latest exhausted request retains its evidence
and holds; no new retry allowance or permanent closure was applied to it.
