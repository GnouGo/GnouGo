# Successful contracts and implementation review corrections

This correction addresses the rejected proposals retained in
[the previous fresh live report](fresh-live-validation-2026-10-04.md).
It keeps the architecture, `mapping.dynamic`, execution oracles and permissions unchanged.

## Changes

- Document MCP publishes and enforces its successful write guarantees: nonempty
  `filePath` and `filePathAbsolute`, nonnegative integer `bytesWritten`, and
  `success: true`. The genuinely optional relative path stays nullable. The existing
  C# host result still represents both success and failure; no planner tool-name
  rule or nullable-binding exception was added.
- Failed writes preserve their complete JSON payload as MCP error content without
  presenting it as a successful `structuredContent` instance. Consumers must honor
  `isError`. Refresh discovery after deployment; changed fingerprints require fresh
  artifact review. See the [producer migration notes](../src/GnOuGo.Document.Mcp/README.md).
- The existing `revise` command accepts optional `preserveRequirements: true` for
  implementation feedback. It checks revision and artifact identity, retains accepted
  intent, discovery, baseline and cumulative accounting, and invalidates generated
  artifacts and approval. Omission preserves existing intent-revision semantics.
  The optional field is omitted from historical command serialization.
- The live harness accepts `--schema-portability revise --revision-command <file>`
  for an unexecuted retained proposal. It archives the old session, result and feedback
  before dispatch. It retains the original planning time/call/repair allowances and
  requires the same frozen candidate. Started execution and unknown pending inference
  cannot be bypassed. Approval acknowledgments are never inferred from feedback.
- Generic generation guidance now binds actual caller values and uses existing
  `always`/`sequence` scopes to preserve requested evidence before cleanup on verified
  failure. This is guidance plus real execution coverage, not proof that arbitrary
  prose has been implemented. No runtime executor changed.

## Deterministic evidence

The new real stdio regression failed against the previous producer's nullable schema
and passes after the correction. It checks actual written contents and denied writes,
including the published Native AOT executable. The local Browser/Document/XLSX suite
now declares a required public output path, covering the previous live contract gap
without changing its cell/visit/failure oracles.

Five parameterized real Flow cases use arbitrary operation and argument names:
success, verified failure, preservation failure, unknown completion and denied approval.
They independently check filesystem contents, operation order and cleanup. Verified
failure retains observed evidence and the original failure; unknown completion blocks
both preservation and cleanup. Denial makes zero external calls. The deterministic
planning adapter uses one call and zero repairs.

Review regressions cover restart, immutable accepted requirements, cumulative usage,
baseline/discovery retention, missing intent, stale revision/hash, tenant/session
isolation, exhausted allowances and refusal to revise started execution. Existing
approval and receipt regressions remain in place.

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Document.Mcp -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror
GNOUGO_DOCUMENT_MCP_TEST_EXECUTABLE="$PWD/src/GnOuGo.Document.Mcp/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Document.Mcp" \
  dotnet test tests/GnOuGo.Document.Mcp.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~SuccessfulWriteContractAndErrorsAreDisjointOverStdio
```

## Live collection boundaries

The next collection uses fresh identities and the unchanged Amazon and code-review
oracles. Retained proposals may receive concrete implementation feedback through the
command above; historical uncertain executions remain untouched. Each artifact still
requires explicit requirement review and separate approval before execution.

The inspected campaign ceiling is €100, including the user's additional €50.
Before this correction's collection, committed/reserved usage was €50.71881488337215,
including the unchanged €1.3033763919821826 unknown-completion reservation.
All planning and execution inference continues through the existing spending gate.
Deterministic tests do not count as live-provider acceptance. Historical cohorts and
the 33/33 benchmark remain unchanged; PR #117 stays draft pending its live gates.

### Retained preliminary candidate

Candidate `0ae72e5933596dbd3ab3414b380319d54cc7b989` exposed a prompt-size
regression in the full solution and CI: two recorded catalog tests exceeded their
unchanged 24,000-token limit (24,017 and 24,148). Compacting generation guidance by
468 characters fixes both; all 20 targeted discovery/review regressions pass with
the original contracts, fixtures and limits. No schema or permission check was relaxed.

The [preliminary cohort c report](evidence/contracts-review-2026-10-04/contractsreview20261004c.json)
retains its fresh Amazon attempt: four calls/attempts, two repairs, two discovery reads,
28,608 input and 12,132 output tokens, €0.451670, 171.843 seconds. The proposal
stopped on a nullable consent selector and missing loop exports. No artifact was
approved or executed. The other five slots are unattempted. This cohort stays separate
from evaluation of the compacted candidate; it establishes no live execution success.

Candidate `2ac36e43676b853d5559689af0b1bd0fc9fcabb8` fits the recorded token
limits, but two existing capability-alternative regressions require the explicit
guidance "compatible alternatives". That qualifier was restored without changing
the tests or limits. The complete planner suite is required before further collection.

[Cohort d](evidence/contracts-review-2026-10-04/contractsreview20261004d.json)
retains two unexecuted proposals: Amazon reached review after three calls and one
repair, but one continuation read per page did not establish completeness. Code
stopped after four calls and one repair on artifact use after release; its plan also
kept report creation on the success path. No approval or external execution occurred.
These results and their accounting remain separate from any subsequent candidate.

## Final tested candidate and live result

Candidate **`36fc937d8a846dce820cb7792ac7c6e19f4b0c7b`** passes the complete
planner suite (873 tests) and the full solution: **4,311 passed, zero failed,
12 expected skips**, with `-m:1 -warnaserror`. Frontend production builds are included.
Release Core/Planning packages, the planning Native AOT smoke, the published Document
stdio regression, published encrypted recovery and planning-skill validation pass.
[Counts and log hashes](evidence/contracts-review-2026-10-04/validation.json) distinguish
overlapping focused tests. The CI deterministic-planner validation job also passes.

**Live execution remains 0/6 and incomplete.** The
[frozen cohort e report](evidence/contracts-review-2026-10-04/contractsreview20261004e.json)
contains one fresh Amazon session and one fresh code session. Both received two
explicit implementation revisions, preserving accepted requirements and reusing cached
discovery. The four remaining repetition slots are unattempted. No artifact was
approved, no workflow was executed, and no new Copilot SDK invocation was started.

| Scenario | Calls / physical attempts | Repairs | Review revisions | Discovery reads | Verified input / output tokens | EUR | Planning seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Amazon | 7 / 8 | 2 | 2 | 2 | 79,579 / 29,708 | 1.148450 | 666.852 |
| Code-review | 8 / 8 | 2 | 2 | 4 | 220,015 / 31,247 | 1.815131 | 426.975 |

Amazon initially compiled but omitted continuation handling. Its first correction added
bounded reads, while publication gating still relied on transform instructions. The
last proposal failed on a nullable consent selector and an unmatched branch output.
The required public workbook path was not loosened or replaced by a fabricated value.

Code initially returned a whole write envelope into an accepted two-field result and
kept reporting on the success path. Its first correction used finalization but still
read failed normal results unconditionally and discarded the actual review result.
The last correction failed `TASK_PRESENCE_SCOPE` for checks inside a nested finalizer.
These are retained proposal/coverage failures; they do not establish failure or success
of the Copilot receipt correction, which was not exercised live here.

[Review feedback and invariant checks](evidence/contracts-review-2026-10-04/review-corrections.json)
record exact revisions/hashes and verify that accepted requirements stayed identical
through every revision. Prior proposals and commands remain in encrypted history.
Both final compiler failures reproduce without inference or saved-session modification:
[Amazon replay](evidence/contracts-review-2026-10-04/amazon-final-replay.json),
[code replay](evidence/contracts-review-2026-10-04/code-final-replay.json).
No further request was sent after either session's attempt allowance was exhausted.

Cohort e costs **€2.963581**; all new usage is verified. Runtime inference and execution
latency are unmeasured because execution never started. Campaign accounting is
**€53.759091 verified + €1.303376 retained reservation = €55.062467 / €100**, leaving
**€44.937533**. Total added cost across this correction's separate cohorts c/d/e is
€4.343653. The stopping limits were proposal validity and per-session allowances,
not the campaign ceiling. No unknown reservation was released or invocation resumed.

Reproduce the retained failures with the frozen source, without provider calls:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --run contractsreview20261004e-amazon-1
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --run contractsreview20261004e-code-1
```

Actual Amazon visits/XLSX output, live Copilot final receipts/report preservation and
cleanup therefore remain unverified. PR #117 stays draft. The implementation and its
deterministic regressions are complete; these live results are not an acceptance claim.
