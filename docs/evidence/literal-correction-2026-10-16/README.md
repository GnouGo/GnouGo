# Executable composition and bounded literal correction

Baseline: `1b77d4fc`, PR #117. All work here is offline. No provider call, live
review, historical retry, approval, financial change or allowance increase occurred.

## Retained failure

Session `4e9f48d922a54e15a09ed4ed6da0577b` remains stopped at revision 18, with
eight model calls and zero repairs. [The replay report](replay-report.json) records
the original request/receipt hashes and validates all eight responses against
their exact issued strict schemas. The call sequence was six discoveries, one
clarification and one proposal. The proposal returned a workflow document through
one `value` task instead of performing the requested review.

[The read-only replay](replay.cs) loads records through public encrypted-storage
APIs and supplies only the eighth committed receipt to a disposable session.
It reproduces `TASK_LITERAL_INVALID` at
`/tasks/define_workflow/outputs/workflowDefinition`, retains calls=8/repairs=0,
and checks every original record again afterward. No provider, MCP client or
historical checkpoint is attached. The compiler correctly rejects `${...}` inside
a TaskPlan literal; the JSON response schema does not establish executable
business composition.

The sanitized [failed proposal](artifact-only-plan.json) retains the invalid
bindings and interpolations. URLs and repository labels are inert fixture values;
the original receipts are untouched.

## Admission and correction

For newly issued initial requests with automatic correction enabled, discovery
reserves a proposal, an available repair and a clarification until answers exist.
The default eight-call ceiling therefore allows five discoveries before a late
clarification. Answers release the reserve, without adding calls. Early proposals
can proceed immediately; repeated clarification can still exhaust the allowance.
Sessions without this policy and already-issued schemas retain their behavior.

| Case | Discovery | Clarification | Proposal | Correction | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| Retained failure | 6 | 1 | 1 | 0 | Literal rejected; 8/8 used |
| Deterministic regression | 5 | 1 | 1 | 1 | Review; 8/8 used, unapproved |
| Repairable literal with contracts available | 0 | 0 | 1 | 1 | Review; 2 calls, 1 repair |
| Repeated invalid literal | 0 | 0 | 1 | 2 | Stop after scoped patch and existing structural fallback |

`TASK_LITERAL_INVALID` now enters correction only when its exact value location
is already editable. The existing patch schema, atomic application and full
validation remain authoritative. Protected guards, host-owned arguments and
independent operations receive no new authority. A runtime input uses, for example,
`{"kind":"input","source":"greeting"}`. Removing or escaping `${greeting}` is not
the correction. Caller data that itself contains `${...}` remains literal data.

Fresh instructions distinguish executable tasks from requested document artifacts;
fresh proposal annotations explain the literal restriction. Existing handwritten
YAML expressions, document-producing operations, compiler v15 and issued prompts
are unchanged. Replacing accepted document outcomes with review outcomes requires
an explicit revision, even if a literal-only patch could make the old plan compile.

The Agent.Server background-owner regressions cover both successful correction and
repeated failure, usage accounting, polling, restart and tenant isolation. Polling
and restart add no calls or approval. A committed correction receipt is reused at
the call limit against the exact issued request.

## Explicit disposable business revision

The [corrected proposal](corrected-proposal.json) and [actual v15 YAML](corrected-v15.yaml)
come from the existing explicit requirement-changing `revise` path, bound to the
disposable revision/hash. The retained session is not revised.

| Contract or operation | Deliberate difference |
| --- | --- |
| Inputs | Exact required string contracts for `pullRequestUrl` and runtime `reviewInstructions` retained |
| Requirements | `workflow_generated` replaced by `review_performed`; the other four outcomes retained verbatim; summary states actual execution |
| Outputs | Remove `workflowPath` and `workflowDefinition`; preserve the original optional, nonnullable string `pullRequestSummary` |
| Operations | Replace the document-only value task with inspection, one clone, checks, review, exact feedback, submission and finalizer cleanup |
| Quality gate | Approve only when producer check results pass and the complete review permits approval; otherwise request changes |
| Summary | Returned from the actual submission result |
| Uncertain completion | No replay or cleanup; reconciliation remains required |

This composition uses real Flow execution, a real local Git MCP clone and four
local command processes. Remote inspection, review and submission use deterministic
adapters; no GitHub review is posted. Exact inline/global comments, repeated findings,
approval/change-request decisions, failed checks, incomplete review, denied cloning,
posting failure and unknown completion are asserted. Restart adds zero effects.

[Composition measurements](composition-metrics.json): two deterministic proposal
responses, zero automatic repairs, zero runtime LLM/provider calls, one review-adapter
call in the successful case; complete request estimates 9,708 and 14,248 against
the retained 24,000 input ceiling; compiled YAML 20,397 UTF-8 bytes. The recorded
32,768 output ceiling is reused, not enlarged. No inference assembles status or
public outputs.

The retained host's exact discovered contracts are listed in the replay report.
They do not establish command/check execution or cleanup capability. The corrected
fixture is an executable example against its test catalog, not a deployable claim
about that host's current permissions or tool inventory.

## Validation and limits

The owning regressions passed before full validation: 29 focused planning checks,
four Agent.Server correction-owner cases, and fourteen code-review executions
(seven existing scenarios plus seven retained-literal counterparts).

Development failures were preserved and corrected without increasing a limit:

- New reserve regressions first reproduced three failures in the old allocation.
- Expanded guidance initially exceeded the existing 24,000 request ceiling
  (24,056, then 24,036). Redundant prose was shortened; the retained discovery
  regression still covers all 74 identities and ten requested contracts.
- The full Planning run caught removal of the phrase “compatible alternatives”
  in two clarification tests (1,460 passed). That guidance was restored.
- The first disposable retained-composition run used the test helper's 12,000
  standalone default. It now reads the retained Agent.Server generation settings;
  all fourteen execution scenarios pass without changing production limits.
- The first full solution attempt on `c80a8d7` found two older v10 composition
  tests still asserting the replaced phrases “branch on null” and “prepend headers”.
  Those assertions now check the equivalent “null branches” and “headers first”
  guidance. No execution, schema, request-size or workbook assertion was removed;
  the failed attempt is retained and complete validation is rerun.

Final solution, packaging, Native AOT, encrypted recovery, skill and candidate CI
results are reported in [PR #117's validation report](https://github.com/GnouGo/GnouGo/pull/117#issuecomment-6215420596).
Browser/Document compatibility checks use local pages and independently inspected
workbooks. No frontend source, compiler lowering, runtime executor or historical
approval format changed.

Predetermined responses prove admission, bounded correction, execution and recovery
mechanics. They do not prove future model compliance, automatic detection of every
missing business action, or live host readiness. The exhausted historical session
cannot acquire another call; a fresh session is required. PR #117 remains draft.
