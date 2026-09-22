# Focused typed repair batches

## Result and scope

Implemented on `feat/deterministic-planner-v2` in `d09206e`, with the fallback-contract
regression and correction in `6f803b9`. The planner architecture, public contracts,
schema-7 storage, interpretation settings, capability eligibility, approval and
publication boundaries are unchanged. This is offline validation, not a new live
reliability result or a completed PR review.

New typed corrections select at most **three non-overlapping targets**, with a
**12,000 estimated input-token limit including the response schema** and an
**8,192 output-token ceiling**. Smaller configured limits still apply. Each
dispatched batch consumes one repair attempt. Initial interpretation and fallback
whole-intent generation keep their configured limits.

## Changes

- `PlanningCorrections.cs` recomputes dependencies from business references,
  scopes, control inputs, subflow outputs and ordering. Invalid upstream inputs
  and producers precede consumers; stable intent order breaks ties. Calculation
  values precede result declarations. Existing topology groups run alone.
- Batches shrink until their complete requests fit. An oversized indivisible
  target stops with `MODEL_INPUT_LIMIT`, its ID/path and estimated size before a
  reservation, call or repair charge. No broad replacement target is invented.
- Requests contain the original user request and host instructions, selected
  fragments and diagnostics, relevant inputs, producer/consumer bindings and
  conditional scopes. Response schemas expose only selected target IDs and the
  definitions those shapes require. Deferred-target counts remain visible.
- `PlanningCapabilityCards.cs` supplies authoritative editable argument contracts
  and referenced value contracts. Projection retains constraints and local schema
  definitions; when detaching a schema would lose constraints, its root and field
  path remain explicit. Fallback corrections receive the authoritative result
  contract even without a downstream consumer.
- Unresolved invocations receive up to four advisory cards ranked from the full
  allowed catalog by purpose and bindings. Invalid arguments do not exclude
  alternatives. The actual zero/one/multiple-choice domains remain complete and
  independently validated.
- Corrections apply atomically, then rebuild and validate the complete graph.
  Partial or rejected edits do not erase deferred errors. Malformed correction
  responses do not manufacture topology targets. All blocking decisions, stale
  approval protection, no-progress detection and cumulative budgets remain active.
- Pending requests bypass new batch construction and use their original targets,
  request identity, schema and output limit. There is no persisted queue or
  dependency map, and no new recovery path for an uncertain dispatch.

## Read-only retained-evidence measurements

The completed interpretation receipts from the two stopped Server attempts were
replayed **in memory under their original response schemas** through public
encrypted KeyVault record APIs. The inspection runtime refuses discovery, new
model dispatch, executable validation and scenario execution. It returns only the
exact completed interpretation receipt and never persists a checkpoint.

The resulting invalid proposals were used solely to size prospective new
correction requests. The uncertain repair itself has no completion receipt and
was neither replayed nor resent. These prospective batches are not replacements
for its reservation.

| Measurement | Earlier `5bcbfd32…` attempt | Uncertain `37edc1f2…` attempt |
| --- | ---: | ---: |
| Original repair targets | 21 | 30 |
| Original estimated repair input tokens | 48,483 | 60,490 |
| Prospective first-batch targets | 1 | 1 |
| Deferred targets | 20 | 29 |
| Prospective prompt bytes | 20,200 | 22,735 |
| Prospective response-schema bytes | 2,026 | 6,921 |
| Prospective estimated input tokens | 7,665 | 10,142 |
| Input-size reduction | 84.2% | 83.2% |
| Prospective output ceiling | 8,192 | 8,192 |
| Blocking diagnostics retained | 175 | 196 |
| Stored reserved calls / repairs | 1 / 0 | 2 / 1 |
| New model dispatches | 0 | 0 |
| YAML or approval produced | No | No |

The selected paths were `/operations/0/value` and `/operations/0`, respectively.
The former defers the same calculation's result declaration until its value has
been corrected. Neither measurement demonstrates that all subsequent batches
would fit or converge within the remaining budget.

Session, request, receipt and budget records retained their payload hashes and
update timestamps. Aggregate evidence digests, including record keys/timestamps:

| Session prefix | Records | SHA-256 |
| --- | ---: | --- |
| `5bcbfd32…` | 4 | `e696febd997c38decf917d167ce5b2b2a3c6ef0f8f4dae39797292c296313e49` |
| `37edc1f2…` | 5 | `c5401590cc20d504faff4c7085e6fccb252576917e0ab744e5637804a3967cc1` |

The pending `37edc1f2…:2:dfe4a8fb…` request still contains **30 targets**, its
original **60,490-token estimate**, and its **32,768 output ceiling**. Its usage
and cost remain unknown. The prior [provider-failure report](planning-server-64k-retry-2026-09-20.md)
retains the dispatch details and known completed usage; no accounting was reset.
Only redacted sizes, locations and digests are recorded here, not private model
responses or capability documents.

## Dedicated E2E configuration

The existing `desktop-planning-e2e` agent was read and updated through normal
`agent_get_by_name` / `agent_update` MCP operations on the isolated Server. The
stored bootstrap's expected hash was checked first; a reread verified the exact
new YAML. Its only change is `max_repair_attempts: 2 → 6`.

| Bootstrap | SHA-256 |
| --- | --- |
| Before | `d88dcf17190269cd15f5cb654d90322f29ba1966ce431e40a152a49bcfb896e7` |
| After | `b08a57f3452ad96d68bd9f0ef7bc331f1abed9c9dfd933a64ececc39c8d30b66` |

The eight-call ceiling, medium reasoning, initial 64,000/32,768 configuration and
existing total-token/time/cost settings are unchanged. New typed repairs receive
the smaller limits above. Other agents and the global two-repair default are
unchanged. Existing sessions retain their own stored two-repair allowance.

The isolated host ran with planning background processing disabled and was
stopped after configuration verification. No chat execution, workflow approval,
PR operation, Copilot inference or publication was started. The unrelated host
and prior campaigns were left alone.

## Validation

Release validation against the final production source `6f803b9`:

| Scope | Result |
| --- | --- |
| Planning | 189 tests passed |
| Flow integrations / encrypted receipts | 76 tests passed |
| Agent.Server / approval, review and security | 345 tests passed |
| Workflow runtime | 853 tests passed |
| AI / HTTP retry and cancellation | 222 tests passed |
| Total | **1,685 passed**, no failed or skipped tests |
| Offline corpus | **8/8**, independent execution and safety gates passed |
| Release Server and benchmark builds | Passed, zero warnings/errors |
| macOS ARM64 Native AOT planner publish and smoke | Passed, eight cases |

`RepairBatchTests.cs` adds 26 sanitized regressions. The first eight were run
before production changes: seven failed and historical-request replay passed.
Coverage includes 3/3/1 splitting, upstream elimination, stable nested scopes,
subflows, cleanup, shrinking/oversized requests, lower configured limits, contract
projection and references, unresolved retrieval with distractors, atomic invalid
edits, partial corrections, malformed responses, restart between batches,
original large-request replay, two/six repairs within eight calls and approval
invalidation. Existing tests retain cancellation, policy/confirmation, executable
artifact bindings, original-schema receipts and durable accounting coverage.

The final contract review added a separately failing fallback-context regression,
then the small correction in `6f803b9`; the complete affected suites were rerun.
No expectations were weakened and no benchmark prompts or production branches
were specialized for the corpus.

Commands (all builds serialized with `-m:1`):

```bash
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.Flow.Tests -c Release -m:1 --no-restore -warnaserror
dotnet test tests/GnOuGo.AI.Core.Tests -c Release -m:1 --no-restore -warnaserror
dotnet build src/GnOuGo.Agent.Server -c Release -m:1 --no-restore -warnaserror
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release -m:1 --no-restore -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -c Release
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
git diff --check
```

Server test/build targets also ran the configured frontend builds successfully;
no frontend source or generated asset changed. No analyzer suppression was added.
The unrelated solution/Python/package release suites were outside this targeted
change. No synthetic live campaign or paid validation was launched.

## Remaining limitations and live gate

Smaller context does not prove lower provider latency or real-model convergence.
Advisory retrieval can still miss a business match; all four cards are context,
not an eligibility or permission decision. Broad topology repairs, large original
instructions or contracts that cannot safely be projected may still exceed a
single-target allowance and stop explicitly. Small batches may exhaust the repair
allowance before all independent failures are corrected.

**Paid PR validation remains paused.** The outstanding provider failure and
uncertain dispatch require a legitimate recovery path preserving reservations,
receipts and conservative accounting. This change neither reconciles that failure
nor creates a replacement session to bypass it. Real Server PR-review execution
and publication remain unvalidated.
