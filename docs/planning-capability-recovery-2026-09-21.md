# Capability selection correction and Designer validation

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

## Result

The targeted corrections are committed as `6428f92` and `b5d0501`. Offline
validation passed. The single fresh Designer test on `b5d0501` **did not reach
FinalReview**: it stopped after one interpretation and two repairs, with
independent contract and expression errors still blocking. No further attempt,
limit increase, workflow approval, execution or publication followed.

The command executor appeared in initial context and was selected using its
issued ID for inspection, checks and cleanup. There was no capability-choice
call or unissued capability string. These improvements do not establish a
successful end-to-end review.

## Changes and deterministic regressions

- `6428f92`: unspecified capabilities receive operation-replacement targets.
  Structural argument compatibility never automatically selects a tool, even
  for a singleton. Value/schema singletons resolve first; unresolved capabilities
  are repaired before ambiguous binding selections. Complete executable
  validation still precedes compilation, scenarios and approval.
- New interpretation schemas admit null or exposed capability IDs, including
  valid revision-context IDs. Operation/group repair schemas admit null or IDs
  from the full allowed catalog. Nested operations and subflows use the same
  constraints. Reserved requests keep their original schemas, identities and
  accounting. Changed fragments are validated before atomic application;
  unrelated invalid references remain blocking.
- `b5d0501`: retrieval uses the best normalized score from the full capability
  document or a nonempty description line. Frequencies count capabilities once;
  repeated lines do not increase weight. Repair advice carries a matching
  excerpt of at most 300 characters. Stable ID ties, 24 initial cards, four
  repair alternatives and input limits remain unchanged.

Sanitized regressions failed before the production edits: reader-only cleanup
domains were selected as executable cleanup, new response schemas accepted
method names, and a relevant operation inside a long description lost to weaker
matches. Tests now cover these failures, null/exact/unknown IDs, revision
context, deferred errors, bounded invalid/unchanged corrections, value-choice
ordering, enum-size budgets, original-schema replay and cumulative accounting.
Existing approval, confirmation, artifact binding and host-failure tests remain.

No public contract shape, schema-7 storage, provider setting, global reasoning
default, retry policy or planner architecture changed.

## Read-only retained-evidence replay

Session `61f2b83cd708439e93085896b31174d8` was inspected through public encrypted
KeyVault records. No model call or session mutation was performed.

For the retained dependency-install repair query, current advice is `cmd_run`,
`cmd_list_allowed_commands`, `copilot_interactive_one_shot`, `copilot_one_shot`.
The command executor was absent from the former four-card list. No method name
was translated into an executable identity.

| Preview | Prompt bytes | Schema bytes | Estimated input tokens | Targets |
| --- | ---: | ---: | ---: | --- |
| Former first value repair | 7,820 | 2,026 | 3,538 | `/operations/4/arguments/2/value` |
| Current operation repair after call 3 | 10,209 | 10,929 | 7,302 | `/operations/5` |
| Capability-only preview rebuilt from the original interpretation receipt | 20,069 | 11,335 | 10,724 | Three operation targets |

The last row isolates unresolved capabilities to inspect available repair
targets; it does not simulate complete convergence or omit errors from the
stored session. All estimates include the response schema and use the existing
planner estimator, distinct from provider-reported usage. The original invalid
intent remains non-executable. Its 20-record digest is unchanged:
`0117c8ccaaa006c298f5fbfdcccf91bbd88aa1264dd79c6d3bedb17692a44a41`.

## Offline release validation

All commands ran on macOS ARM64 with .NET 10.0.300. The final source revision was
`b5d0501`; subsequent documentation does not receive a separate live evaluation.

| Scope | Passed |
| --- | ---: |
| Planning | 234 |
| Flow | 853 |
| Flow.Integrations | 76 |
| Agent.Server | 368 |
| AI.Core | 222 |
| Total | 1,753 |

Each component used `dotnet test tests/<project> -c Release -m:1 --no-restore
-warnaserror`, with projects `GnOuGo.Flow.Planning.Tests`, `GnOuGo.Flow.Tests`,
`GnOuGo.Flow.Integrations.Tests`, `GnOuGo.Agent.Server.Tests` and
`GnOuGo.AI.Core.Tests`. Release builds and invoked frontend builds emitted no
warnings. No tests were skipped.

Additional checks:

```sh
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -m:1 --no-restore -p:PublishAot=true -warnaserror -o <isolated-aot>
<isolated-aot>/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 \
  --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false \
  -p:SkipBundledServerTools=true -p:UseAppHost=true -warnaserror -o <isolated-host>
<isolated-host>/GnOuGo.Agent.Server --planning-persistence-smoke <isolated-data>
```

Both publishes were warning-free. Native AOT passed all eight offline cases
(`local`, `read_transform`, `nullable_defaults`, `conditional`, `collections`,
`protected_cleanup`, `review_french`, `review_distractors`), each with one mocked
interpretation and zero repairs. Published persistence checks passed schema-7
encrypted storage, tenant isolation and uncertain-publication replay. These are
offline checks, not live-model reliability measurements.

## Single live Designer test

An isolated, freshly published Server and Chromium profile used real
KeyVault-backed integration discovery and the configured provider. The empty
Designer index prevented background resumption of older sessions. The normal
Designer form submitted the original 680-byte prompt unchanged, once. Its hash:
`8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`.

| Setting | Value |
| --- | --- |
| Source | `b5d0501` |
| Session | `12dc220b7efa47ea9753d54f01ae2baf` |
| Name | `designer-capability-20260921-b5d0501` |
| Submitted / stopped | 2026-09-21 19:07:19 / 19:08:33 UTC |
| Provider / model / protocol | OpenAi / `gpt-5.5-2026-04-24` / Chat Completions |
| Reasoning | Low for this isolated test only |
| Maximum calls / repairs | 8 / 2 |
| Input / output limits | 12,000 / 8,192 tokens |
| Final state | Stopped, revision 8; no pending request |
| Elapsed / active time | Approximately 73.9 / 48.6 seconds |
| Catalog / exposed cards | 129 / 24 |

All three logical calls have completed durable receipts and one HTTP attempt
returning 200. No timeout, truncation, uncertain dispatch or input-limit failure
occurred. Provider-reported usage is complete for these three calls.

| Call | Phase | Input tokens | Output tokens | Duration | Estimated USD |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Interpretation | 8,168 | 4,296 | 28.76 s | 0.169720 |
| 2 | Repair 1 | 5,509 | 848 | 9.08 s | 0.052985 |
| 3 | Repair 2 | 5,508 | 286 | 3.46 s | 0.036120 |
| Total | | 19,185 | 5,430 | | 0.258825 |

Stored estimated cost is **EUR 0.225261**, totaling 24,615 reported tokens.
Reported reasoning tokens are included in output usage; private reasoning
content was not inspected. Historical charges remain separate and unchanged.

The interpretation proposed parallel PR-context reads, one clone, repository
inspection, a model-generated verification plan, conditional dependency/check
commands, a review transformation, publication and cleanup. These remained
proposals. `cmd_run` appeared in initial context and was selected by exact ID,
including cleanup. `review_evaluate` and `review_publish` remained in the full
catalog but outside the initial shortlist. Publication stayed unresolved and
no host evaluation invocation was generated.

## Remaining failures and limits of this result

The two repairs addressed `/operations/1/branches/0/body` and
`/operations/1/branches/1/body`, with respectively 30 and 29 deferred targets.
These were existing atomic topology targets, one per call. The first added a
local metadata calculation with optional typed fields; the second added a
string-typed diff calculation. They did not fix the other independent errors.

| Classification | Durable evidence |
| --- | --- |
| Untyped-result boundary limitation | Several `pull_request_read` results have no declared output contract and cross parallel branch boundaries. Two local declarations leave other opaque branch results unresolved. No authoritative schema was invented by the engine. |
| Invalid intent | `determineVerificationPlan` and `reviewDecision` have no result type, although their consumers require fields. Conditions reference undeclared names such as `verificationPlanRequiresPnpmInstall`; their only supplied parameter is `verificationPlan`. Conditional outputs are also consumed as unconditionally available. |
| Capability recovery incomplete | Publication remains null, and evaluation/publication capabilities are outside initial context. Upstream contract repairs consume both attempts before that operation can receive focused advice. This run does not demonstrate a new ranking defect for that operation. |
| Provider/transport | No failure observed; all requests completed with reported usage. |

The final state retains 86 blocking diagnostics and 11 holes: 11
`HOLE_UNRESOLVED`, 1 `STEP_TYPE_DENIED` for the unresolved placeholder, 17
`BINDING_UNAVAILABLE`, 7 `COMPUTATION_BINDING_INVALID`, 9 `SCHEMA_INVALID`, 2
`STRUCTURED_OUTPUT_INVALID`, 28 `OUTPUT_REFERENCE_INVALID`, 2
`SCHEMA_REFERENCE_INVALID`, 2 `OUTPUT_TYPE_MISMATCH`, and 7
`VALUE_LOWERING_INVALID`. Several are consequences of the same producer errors;
they are not 86 independent defects. No executable YAML or scenarios resulted.

This evidence does not establish a deterministic builder defect or justify an
architecture change. Further work would need a separate generic reproduction
of the remaining producer-contract/expression problems. No additional production
change or live attempt followed this failure.

## Trace inspection, preservation and cleanup

The actual Designer displayed the stopped session and its diagnostics. The
Pipeline / LLM calls panel showed all three completed journal receipts. Selecting
the interpretation trace and expanding its document displayed retained inputs,
response schema, output, byte breakdown and usage. Repair traces showed one
target per call, deferred counts and HTTP 200 attempts.

| Phase | Trace |
| --- | --- |
| Interpretation | `582b3573431745ca0ed4fe8fc58c689a` |
| Repair 1 | `68dad98fd619c7e5110f953fa93ea20f` |
| Repair 2 | `c275916006c4202a2e9a6de5bb4620e3` |
| Stop | `1acb3e503c65d3ae9971c789cb20b8be` |

The new session's 16 encrypted session/request/receipt/budget records have digest
`556bdf60e28c12dbe582d993ea3e7f466804f0e469d06729d9f737f675907d72`.
The earlier sessions `aa6971c5…`, `3fbbd97b…`, `7321f76a…` and `61f2b83c…`
retain the exact digests recorded in the
[preceding report](planning-designer-low-2026-09-21.md).

The trace panel was closed and the isolated Server and Chromium processes were
stopped. Their persistence directories and encrypted evidence were retained.
No repository was cloned or changed, no workflow was approved/saved/executed,
and no GitHub review, metadata change, source push, merge or deployment occurred.
No synthetic evaluation campaign was started.
