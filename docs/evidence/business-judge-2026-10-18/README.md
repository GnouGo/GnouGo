# Optional business review and bounded convergence — PR #119

This delivery prioritizes deterministic admission, complete revalidation and scoped
correction. The final implementation candidate is
`cf47affd4543652a4fd091d3cd623868158f2934`. Business review is an explicit creation option, disabled by default.
Its independent call uses the generator's model and the existing session journal
and allowances. A model finding never establishes deterministic correctness or edit
authority. No paid inference or externally effectful live evaluation was run.

## Implementation boundary

- Fresh sessions use 12 calls, four corrections, 32,000 input tokens and 32,768
  output tokens per planning request. Saved settings and sparse historical defaults
  remain unchanged; omitted fields deserialize as 8/2/12,000/8,192.
- Complete serialized request admission includes the prospective request identity,
  response schema and output reservation. Unknown required model capabilities stop
  dispatch. Input estimates and conservative extraction byte bounds retain distinct
  labels. Source sizes known at planning time are descriptive; runtime input fit
  remains unverified until the complete request and effective allowance are known.
- The optional Judge sees request/revisions, accepted answers, requirements,
  discovery limitations, inspected contracts and the TaskPlan. It has no execution
  tools or test oracle. Structured references are checked, findings are displayed
  separately, and no requirement acknowledgment is selected automatically.
- Only previously explicit edit authority admits a Judge-guided correction. Other
  findings require explicit revision. Revalidation, cumulative counters, one
  structural regeneration maximum and repeated-candidate stops remain in force.
- Versioned private review results are reused for an unchanged context. Receipt
  recovery uses the issued request and schema even if current presentation changes.
  Uncertain completion blocks redispatch. Format 10, compiler v15 and TaskPlan wire
  contracts are unchanged.

The planning skill was the first changed file; its validator, local links and
architectural consistency were checked and it was reread before code changes.
Scenario-specific content remains in tests and evidence.

## Frozen scripted comparison

[Manifest](manifest.json) records the compiled candidate binaries, fixture/oracle
hashes and settings frozen before the three fixture arms.
[Commands](commands.json) records the three invocations.
[Candidate verification](candidate-verification.json) confirms the relevant compiled
assemblies and recorded source hashes still match that frozen comparison.
[Baseline](baseline.json) records immutable Git objects from
`04a9bc0241a1d9c5400def8eb658dcd0f22768c5`; these baseline hashes were read after
implementation, without rewriting retained evidence. This is a candidate mechanism
comparison with independent existing execution oracles, not a baseline/candidate
live-model evaluation.

Each arm runs the eight existing business cases three times. Local-file and retained
product/code negative variants are covered separately by the component and published
smoke suites. The fixture Judge returns an explicitly scripted no-findings response.

| Fixture arm | Runs | Correct first proposals | Execution oracle / convergence | Logical calls | Median calls | Corrections |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| New defaults, Judge off | 24 | 24 | 24 | 24 | 1 | 0 |
| Same new defaults, Judge on | 24 | 24 | 24 | 48 | 2 | 0 |
| Previous host defaults, Judge off | 24 | 24 | 24 | 24 | 1 | 0 |

[Summary](summary.json), [control rows](control.jsonl), [Judge rows](judge.jsonl),
and [previous-default rows](prior_host_defaults.jsonl) preserve every outcome.
The [initial candidate](initial-candidate/manifest.json) and
[constructor-corrected candidate](constructor-candidate/manifest.json) retain their
own manifests and rows. The complete 72-run comparison was refrozen and rerun on
`cf47affd4543652a4fd091d3cd623868158f2934` after the test and benchmark follow-up.
The table above describes that final candidate: every arm passed its independent
execution and convergence checks. All three candidate cohorts remain separately
identifiable; earlier outcomes were not reassigned to the final commit.

Tokens, physical provider attempts and costs are unknown in these scripted runs,
represented as null. They are not zero-cost live observations. No safety violations
or introduced regressions were observed by the fixture oracles.

Zero false positives/negatives in this all-correct, scripted cohort do **not** measure
the sensitivity of a real Judge. Separate deterministic regressions exercise false
positives, missed omissions, unknown labels, malformed/invented references and
repeated incorrect candidates. Comparison reports isolate Judge activation at equal
budgets from changes to defaults; incomplete or incomparable live cohorts remain
inconclusive. These rows have no provider/model identity, so they are descriptive
fixture evidence rather than a successful strict live-comparison report. No general
reliability gain or 25% first-proposal improvement is claimed.

## Validation and retained failures

[Final serial validation](validation.json) records **6,124 passed, 13 skipped and
zero failed tests across 33 assemblies**, using `-m:1 -warnaserror` on candidate
`cf47affd4543652a4fd091d3cd623868158f2934`. Seven skips require native Windows
PowerShell, one is the opt-in deterministic browser smoke, and five require
explicit live-model evaluation. No paid inference was run.
[Release validation](release-validation.json) records successful Native AOT publish
and smoke, the trimmed self-contained server and encrypted-recovery smoke, and five
Release packages, all on production commit
`6fe38c2cc05f7541d5b3db21dddf080452b8b092` with `-warnaserror`. Existing
publish-local dependency exceptions are unchanged; no suppression was added.
The follow-up commit `cf47affd4543652a4fd091d3cd623868158f2934` changes tests and the
benchmark receipt adapter only; its production implementation is unchanged from
the tested Release commit.
[Frontend validation](frontend-validation.json) records both successful production
builds. Flow includes TypeScript checking; neither package defines separate test or
lint scripts. Razor interactions are covered by host tests; the existing opt-in
browser clarification check is not enabled.

The initial full-suite run
exposed test fixtures relying on implicit historical defaults or model clients that
did not declare capabilities. Their intent, contracts and execution oracles were
retained; historical limits are now explicit and fake clients declare their supported
settings. Fresh scripted continuations from recorded contracts use the authorized
32,000 allowance; original issued recordings remain immutable.

A later serial delivery attempt completed the long historical replay cases, then
exposed further host fixtures relying on undeclared model capabilities or implicit
historical defaults. A `ChatPlanningChoice` bridge fixture subsequently kept waiting
after its initial planning state had stopped. Only the Agent.Server test-host process
was terminated so the remaining solution projects could continue. This was a local
automated bridge test, not a live-provider evaluation; its incomplete host run remains
failed evidence even if subsequent projects pass.

Follow-up corrections make the affected fixture capabilities and historical limits
explicit. The benchmark-only receipt adapter also reuses an already completed receipt
before attempting fresh request admission; replay must not require a new dispatch or
new capability metadata. These changes do not alter the independent business oracles,
production admission policy or retained issued requests. Focused revalidation and a
complete serial-suite pass are separate from earlier package and published-smoke
successes.

The recorded-product fixture now distinguishes a fresh synthetic request from
historical replay. Fresh requests use the authorized 32,000-input-token creation
default and measure the complete serialized request, including its envelope and
response schema. Historical replay retains its 24,000 allowance and original issued
schemas. The mapping-failure fake declares its supported capabilities, and its
interaction wait has a bounded timeout; the chat bridge asserts the expected
clarification state before waiting for an answer. Focused recorded-product and
mapping-failure isolation checks pass. The focused host run excluding
`LocalProductOutcomeExecutionTests` passed 886 tests, with one opt-in test skipped.
The final complete solution run independently reran all 228 product-execution
cases and completed the host assembly with 1,114 passed and one skipped test.
Earlier failed and incomplete attempts remain separate evidence; their results
are not combined to manufacture a complete-suite pass.

Focused regressions also reproduced and fixed invalid JSON Pointer escapes,
presentation-dependent Judge receipt replay, authorized descendant-slot routing,
premature correction completion before Judge review, and request identity bytes
being omitted before reserving a call. A final constructor regression also verified
that a directly supplied model can declare its own capabilities while explicit host
capabilities remain authoritative. Unknown completion and unrelated edits remain
blocked. [Earlier failed and incomplete attempts](validation-attempts.json) retain their
log hashes, causes and failing test names rather than being described as successful
runs.

Existing Amazon failures and live evidence remain untouched. A future live comparison
requires separate authorization, frozen model/settings/contracts/oracles and the
existing cumulative financial ceilings. Judge activation remains an explicit choice.
