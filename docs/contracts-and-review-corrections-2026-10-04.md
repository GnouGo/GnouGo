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
