# Discovery continuity and convergence qualification

Implementation source: `2a83c9c09c2dedeb481f15d19eb6ce74c3670797` on
`feat/incremental-taskplan-construction`, PR #120. The implementation and tests are
committed locally; the PR remains unmerged. The user's staged solution and
`.vscode/settings.json` changes are preserved and excluded from these commits.
The exact solution used for validation is identified by SHA-256 in
[the validation manifest](validation.json).

The current snapshot path did **not** reproduce catalog drift across transitions.
A generic source with 48 capabilities retained four eight-item pages, disposed the
actual Server host, then continued through a fresh host/runtime against the same
encrypted storage. All eight Strict/Diagnostic variants covered unchanged metadata,
reordered JSON properties, a semantically changed remote contract and an unavailable
remote source. Continuation used the pinned original snapshot: six pages, 48 distinct
capabilities, one initial metadata capture, zero subsequent remote fetches and zero
model calls. A separate test exited one OS process and continued in a different one
with the remote source unavailable. This is regression evidence for the existing
snapshot mechanism, not evidence that restarting caused the reported incident.
See [host identities](controlled-host.json) and [cold-process evidence](cold-process.json).

The independently demonstrated loop lost applicable rejection feedback between
implementation selection and discovery. In the retained operator session, call 247
had the reason, call 248 selected `value` without it, and call 249 abandoned the draft;
the accepted plan baseline remained unchanged. The new unrelated, renamed-capability
regression reproduced the missing discovery context before correction. The exact
reason and its retrievable evidence now reach implementation selection, discovery
and drafting only while block, scope, original obligations and baseline match.
Historical findings remain retrievable after applicability expires. No rule chooses
a provider, tool or fragment kind by scenario, and a copied value does not establish
a communication effect. See [reproductions and retained loop measurements](regressions.json).

Other reproduced corrections preserve the existing mechanisms:

- New `json-v1:` comparison versions canonicalize object-property ordering and
  whitespace. Arrays, values, descriptions, effects, constraints and metadata remain
  significant. Exact payload integrity hashes and original legacy verification remain
  separate; old issued bytes are not rewritten. This is not general schema equivalence.
- Explicit refresh exposes only its active action in current operation directories,
  preserving old pages and versions as history. This fixes empty/ambiguous directories
  caused by mixing refreshed and historical versions.
- Selected-contract failures retain structured causes and a durable required-contract
  blockage after the model receipt is retained. Existing protected recovery can
  refresh metadata without inference, then waits for separate explicit resumption.
  A first verified restoration of the exact selected contract, or its actually
  changed version, distinguishes a subsequent binding decision. Unchanged repetitions,
  random action IDs and unrelated rows do not reset repeat guards.
- Removed sources remain real catalog changes during revalidation. Transport failures
  retain their distinct safe cause. Accepted independent work, business responsibility,
  original policies, charges and reservations remain unchanged; changed accepted work
  still requires existing scoped revision and dependency/review invalidation.

One TaskPlan, compiler, technical validation and business judges remain authoritative.
Permissions, approvals, cancellation, physical provider limits, financial admission
and uncertain-replay barriers remain mandatory. Diagnostic optimization/nonprogress
thresholds remain warnings under their captured authority. Configured phase allocations
remain independent of that authority; no forced model maximum was introduced.

The actual Server browser checks exercise warnings, protected recovery, reload,
required-contract blockage, cancellation, finite admission and shared inspection.
Recovery itself dispatches zero inference. These are controlled-provider fixtures;
the recovery fixture's 70 completed calls and two checkpoints are seeded retained
history, not paid generation. [Browser results](controlled-browser.json) and
[screenshot identities](screenshots.json) retain that distinction.

| Controlled Agent.Server screenshot | Evidence |
| --- | --- |
| [Active planning phase](screenshots/controlled-active-phase.png) | Controlled HTTP provider |
| [Expanded shared LLM exchange](screenshots/controlled-shared-exchange.png) | Actual serialized request and retained response; encrypted/redacted reads |
| [Warning permits continuation](screenshots/controlled-warning-continues.png) | Diagnostic authority; mandatory checks retained |
| [Required-contract recovery waiting for explicit resume](screenshots/controlled-required-contract-recovery.png) | Seeded history; no model dispatch during metadata recovery |

Fresh live qualification used the configured MCP source through the actual
Agent.Server host: **52 distinct capabilities, seven pages, one runtime recreation
after page four, zero provider/model dispatches**. The persisted snapshot continued
successfully. This pagination qualification is independent of Amazon's business-source
selection. It is live metadata evidence, not an Amazon generation/execution or
business oracle result. [Live metadata report](live-metadata.json).

The fresh deployment comparison matches the registered configuration: `OpenAi /
gpt-6.1-sol-2026-09-29`, exact model name retained, configured metadata declaring
structured-output and medium-reasoning support, matching host/metadata fingerprints
and zero configuration differences. Actual provider acceptance awaits the funded probe.
No provider/model fallback or configuration amendment was needed. Endpoint origins and
credentials are omitted from the public [configuration report](configuration.json).

Paid continuation is blocked by **fresh incomplete financial evidence**, not an old
configuration mismatch. Read-only owned journal inspection covered both operator
sessions. Of 327 complete receipts, 325 report cache-write usage without a captured
cache-write tariff. Session `9fce45f8750a4ed19a3fe1b5ba48adb3` retains request 258
without a completed receipt; it is neither replayed nor treated as free. The saved
status is `generating` while the app is offline; this investigation did not cancel,
resume or mutate it. The older session's request and reservation also remain retained.
Missing prices were not backfilled from today's catalog or inferred from input pricing.
An additional exact owned-usage scan found zero provider charge/cost fields in all
327 normalized and raw provider usage records. That retained evidence supplies no
alternative billed total; no external invoice API was called.
[Billing-field scan and reader fingerprint](billing-fields.json).

| Financial evidence, EUR | Exact retained amount |
| --- | --- |
| Global ceiling | 740.0301038327063045424449473 |
| Campaign known captured-tariff estimated cost | 475.28093332377412455040481684 |
| Campaign reservations | 222.7236032944974286315167358 |
| Earlier additional Server-local retained hold | 1.9552387740555951532430506058 |
| Two operator sessions' uncertain reservations | 5.0543478260869565217391304347 |
| Remaining upper bound **before unpriced operator consumption** | 35.0159806142921996855412136195 |
| **Verified available global budget** | **Unavailable** |

The upper bound is not spendable verified funding. The read-only accounting preflight
cannot prove coverage for a new probe; it is not an HTTP provider refusal. Therefore
this change dispatches **zero paid structured-output probes, zero real-LLM/controlled-MCP
workflows and zero real Amazon executions/oracles**. No live convergence or cost-saving
claim is made. The first remaining obstacle is historically applicable cache-write
cost evidence (or an authoritative provider charge/upper bound), while preserving
unknown reservations and exact receipt recovery. No Unlimited campaign, cap increase,
reservation release, historical repricing or manually edited generated plan was used.
See [fresh funding evidence with exact decimals](funding.json).

Activate updated local binaries with:

```sh
dotnet run --project src/GnOuGo.Agent.Server
```

Use the protected operator window. Eligible completed-journal failures offer the
existing source refresh/continue commands, then explicit resume; unresolved model
dispatches remain ineligible. The Diagnostic launch profile remains an alias. These
changes do not migrate or automatically resume historical sessions. Instructions and
the affected planning/.NET skill rules are updated.
Paid qualification must use the existing Limited campaign harness and complete global
funding/replay admission; opening the ordinary application establishes no campaign
funding or authority to use its separate application budget defaults.

Final qualification used the unchanged implementation source above:

| Check | Result |
| --- | --- |
| Full Release solution build, warnings as errors | Passed, zero warnings/errors |
| Full solution, all 33 test assemblies | **7,936 passed, 0 failed, 13 skipped**; exit 0, 4,475.399 seconds |
| Planner suite, included in solution total | 2,759 passed, no failures/skips |
| Agent.Server suite, included in solution total | 1,648 passed, no failures, one existing opt-in skip |
| Flow.Integrations suite, included in solution total | 116 passed, no failures/skips |
| KeyVault.Core suite, included in solution total | 19 passed, no failures/skips |
| ClientApp production build | Passed |
| Core, Planning and Integrations Release packages | All three passed; not uploaded |
| Planner Native AOT publish and execution, macOS arm64 | Passed; deterministic responses, exact review recovery and independent execution oracles |
| Native CLI publish and exact diagnostic audit | Passed under existing pinned dependency policy; two exact EF Core experimental notices, no unexpected diagnostics |
| Published native CLI encrypted-storage smoke | Passed: tenant isolation, receipt reuse, index rebuild, revisions and no plaintext workflow content |
| Agent.Server existing Release partial-trim publish | Passed, zero emitted diagnostics; managed application, not whole-application Native AOT |
| Published Server planning-persistence smoke | Passed: encrypted restart, exact request/receipt recovery and owner/revision checks |

[The manifest](validation.json) retains commands, UTC times, original and sanitized
log hashes, TRX identities/counters, skip reasons and binary/package hashes.
[Per-test results](test-results.tsv.gz), compressed logs under `logs/`, and
[toolchain identities](toolchain.json) make the results source-bound. Focused runs
overlap these totals and must not be added together.

Two publication command failures are retained alongside their successful corrected
commands. The initial CLI command's global MSBuild `-warnaserror` promoted the two
already audited EF Core Tasks experimental notices. The rerun keeps compiler warnings
as errors and applies the repository's existing exact-notice policy; its
[included audit](publish-audit.py) rejects every other diagnostic. The initial Server
probe forced `PublishTrimmed=false`, disabling SDK configuration-binding generation
in the AOT-compatible bundled Git project. The existing Server Release profile
enables that generator and passed. No source suppression, warning baseline or component
policy was changed. Existing Jint/EF publish-local boundaries remain in place.

The 13 full-solution skips are seven native Windows PowerShell cases on macOS,
five opt-in paid Copilot/GitHub cases, and one older clarification-browser opt-in
whose `PLAYWRIGHT_MODULE_PATH` was not enabled. The new actual-host browser fixtures
resolve their staged Playwright module and ran successfully. ClientApp declares no
separate test/lint/type-check scripts; its production build and the actual-host
browser checks were run. `pwsh` is not installed, so the full multi-component
PowerShell publish matrix was unavailable; targeted packages/native publications
and the included exact diagnostic audit ran instead. No Windows or whole-Server
Native AOT success is claimed. Paid workflow/oracle qualification remains blocked
by the fresh financial evidence above.

Intermediate failed and interrupted solution runs remain separately labelled in
[the regression evidence](regressions.json). The final full-solution pass is attributed
only to `2a83c9c0`, not to an interrupted predecessor or the later documentation commit.
