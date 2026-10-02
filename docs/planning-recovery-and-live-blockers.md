# Safe planning recovery and live execution blockers

> Historical implementation evidence. The outcome annotations and repair versions described below are superseded by [business TaskPlan simplification](planner-simplification.md). Measurements and failed cohorts remain unchanged.

## Reproduced failure

Designer session `40944cd07bd54be5823f47ad019e131e` retained a valid response despite
showing `LLM_BUDGET_UNVERIFIABLE`. On 2026-10-02 (Europe/Paris), its request was reserved
at 14:42:59, another coordinator stopped the session at 14:43:00, and the original
request completed at 14:43:06 with 6,046 input and 439 output tokens. Its response
requested discovery. Replaying that receipt avoids the first dispatch; subsequent
planning still requires further calls. The original session and workflow remain
unchanged, with no automatic rerun or approval.

## Changes and compatibility

Designer coordinators now acquire exclusive OS file leases beside the EF database
before loading/advancing a session or applying commands and approvals. Hosts sharing
the database must share this lease directory on a filesystem supporting exclusive
locks. Contention leaves checkpoints alone. A separate encrypted cancellation record
signals the owner across processes. Receipt/accounting flushes after a completed
response have independent bounded cancellation so a simultaneous cancellation does
not erase verified usage.

Inspection distinguishes active requests, available receipts, unknown completion and
provider rejection. **Resume from saved response** verifies tenant/revision, original
request hash and schema, dispatch index and cumulative accounting before consuming
the receipt. It makes no new reservation. Unknown completions retain the explicit
retry path and conservative usage; provider rejections require a corrected new
session. Artifact approval remains a separate action.

Fresh sessions and explicit intent revisions use outcome review version 3. The
model selects an inspected, policy-allowed operation; its contract supplies the
effect. `placement: normal|cleanup` maps to existing `Always` storage. Optional
public-input references support data-only outcomes, never external execution.
Exact operation evidence, path coverage, loops and cleanup remain validated.
Version-6 repairs reuse available exports, expose only missing named chains and
conditional counterparts, and constrain references to visible compatible sources.
The complete authority is fingerprinted. Historical schemas, outcome versions,
repair envelopes, approvals, TaskPlan/PlanningGraph and storage format 10 remain.

Long interactive Copilot work uses MCP Tasks 2.2.0, with one logical result rather
than the ordinary MCP client's ten-round exchange. Producer-owned KeyVault records
and non-renewable host ceilings preserve evidence, questions, refusals and accounting.
Successor SDK sessions require verified quiescence; unknown work stops for
reconciliation. Unknown completion fails the MCP task rather than returning a normal
partial result; the Flow execution journal therefore blocks cleanup and redispatch.
Partial evidence remains encrypted. Business questions stay interactive, and no grant or host permission
is broadened. Bounded `agent.run` retains its single-turn authority.

## Validation and reproducibility

The pre-change full solution passed 4,040 tests, with 13 opt-in skips and no failures.
Focused regressions exercise competing coordinators, durable cancellation with a
late receipt, restart/tenant isolation, original-schema resume, receipt accounting,
contract-derived effects, existing/missing export chains, per-item coverage, and
atomic repair authority. Actual in-process MCP transport tests use encrypted storage
and deterministic SDK adapters, including 14 human-input rounds with a refusal,
finite interaction/session limits, verified continuation, cancellation and abandoned
owner recovery. These are deterministic execution tests, not live-provider evidence.

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
PLAYWRIGHT_MODULE_PATH=/absolute/path/to/playwright/index.mjs dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~PlanningClarificationBrowserTests
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 -m:1 -warnaserror
```

Validation also passed the Designer keyboard/mobile browser check, production frontend
build, six independent Release packages (Flow Core/Planning/Integrations/Copilot/Persistence
and Copilot Core), planning and Copilot Native AOT smokes, published Copilot stdio discovery,
and encrypted recovery in the trimmed Agent.Server binary. The server publish skipped
bundling tools, which were built and checked independently. Linux/Windows publication
remains a CI check; local validation used macOS ARM64. The linked readiness helper is also compiled by Agent.Server.Tests; CI caught its
initial missing source link, which is corrected. Final live results follow collection. The existing `schema-portability-20261002` EUR 50 campaign is retained:
the pre-dispatch ledger has 102 physical attempts, 1,111,677 verified input tokens,
173,498 verified output tokens and EUR 9.526752522570366 in estimated cost, with zero
unknown transport reservations. Historical diagnostic inconclusiveness is retained.
The historical 33/33 benchmark and failed final4 cohort are unchanged and do not
validate this correction. The six new Amazon/code execution oracles must all pass
before PR #117 is marked ready.

## Rollout

Rebuild and restart updated Agent.Server and Copilot MCP hosts, then refresh discovery.
Use fresh sessions for new outcome annotations. Existing stopped sessions remain
stopped until explicit saved-response recovery or conservative retry. Do not replay
unknown external work or copy permissions between agents. Live feedback and proposed
diff comments remain local; GitHub review publication is excluded.

## Retained diagnostic executions

On candidate `2aedd3db`, Amazon reached review in two calls without repair. Its
generated loop bound was one despite requesting up to three products; the runtime
rejected the larger collection and closed the browser. The existing deterministic
oversized-collection regression reproduces this safe rejection. No approved bound
was silently widened.

Code reached review in four calls and one repair. Its interactive Copilot call
completed with 21 tool observations and more than ten individual permission
exchanges, without the former MCP limit. Required full-check/install/version
evidence remained absent. A subsequent bounded agent rejected its 362,996-token
conservative input reservation against an approved 20,000-token allowance before
inference, then the workflow cleaned up. No missing checks were called successful.
Generic generation guidance now requires delegated instructions to retain all
requested checks/constraints and discourages duplicate transcripts in downstream
agents. This is guidance, not a proof of natural-language equivalence.

[Sanitized diagnostic measurements](evidence/planning-recovery/diagnostic-recovery1.json)
retain both failures separately from the final cohort; full proposals and execution
observations remain in the encrypted campaign.

The superseded `recovery2` cohort exposed a compiler defect: a conditional exported a
typed array on one branch and an empty array on the other. The compiler represented
both correctly as alternatives, but loop validation required a direct `type: array`.
Two model repairs could not resolve that valid binding. Element resolution now considers
all array alternatives, excluding impossible elements from literal empty arrays, while
the runtime guard retains the complete collection contract. Unknown elements remain
opaque; non-array branches and incompatible consumers are rejected. The original
retained proposal compiles and passes graph validation without a model call or repair.
Eight generic regressions cover nested exports, sequential/parallel execution, both
branches, unknown contents and incompatible alternatives; Native AOT also executes
both branches. No saved session or approval is changed by the read-only replay command.

The other executed `recovery2` Amazon run returned a status-only workbook after its
model found no product URLs in a truncated page response. Execution completed and
cleanup ran, but independent workbook validation failed. That remains a failure;
successful compilation or truthful missing-data reporting does not satisfy the
requested spreadsheet oracle.
