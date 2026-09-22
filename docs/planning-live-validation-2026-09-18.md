# Agent.Server live planning validation — 2026-09-18

## Result

The schema-6 refactor was committed and pushed as `ff567a2` on `feat/deterministic-planner-v2`. The user's unchanged French PR-review prompt was submitted through the actual Agent.Server planning API using its configured model and KeyVault-backed integrations.

The persisted live sessions **have not reached final review or execution**. The original session remains stopped at seven calls and two repairs. A separately authorized session on `2d32387` exposed two host schema defects, now corrected. Its last completed candidate passes offline validation and all 27 isolated scenarios without another model call. The live session remains stopped because a subsequent repair has no durable completion receipt and both repair slots are consumed. Offline replay did not alter the session or bypass that accounting boundary. No generated YAML was substituted by hand, and no PR-specific planning path was added.

No repository clone, dependency installation, project test execution, or GitHub publication occurred. Consequently this run provides no evidence that the target pull request passes any requested check. Workflow approval, runtime publication confirmation, and a fresh head check remain outstanding. Private repository content, credentials, model responses, and raw host logs are excluded from this report.

## New live session on `2d32387` — September 18–19

Agent.Server was restarted with its configured model and KeyVault-backed integrations. The original encrypted prompt was recovered through public KeyVault APIs and submitted unchanged through `POST /api/planning`. Its SHA-256 was `eb96e4a486a9d10137f1e67e6eabb10534ba0cdb86e58811e79d438531007b60`. No replacement session was created after this new session stopped. The previous session and its accounting were untouched.

The new session retained medium reasoning, eight calls, two repairs per submitted intent, 96,000 input and 32,768 output tokens per request, 15,000,000 total tokens, EUR 50 and 18,000,000 milliseconds active time. Discovery used no model call and included `review_evaluate` and `review_publish`. The configured GitHub catalog exposed declared read effects only; raw writes and unknown effects were excluded.

| New-session transition | Observed outcome |
| --- | --- |
| Call 1: interpretation | Unresolved schema holes, optional-value bindings and exported host schema defects |
| Host schema fix and same-session resubmission | Exact prompt retained; cumulative call and usage retained; catalog rediscovered |
| Call 2: interpretation | Duplicate structured-output declaration and optional-value bindings |
| Call 3: first repair | Those errors corrected; one nested nullable output-contract error remained |
| Call 4: second repair | Request reserved; no durable completion receipt |
| Restart/read-only inspection | Revision 11, stopped with `LLM_BUDGET_UNVERIFIABLE`; no redispatch |
| Offline validation after the nullable-contract fix | Zero diagnostics; 27 scenarios passed; compiler produced 30,427 characters of YAML in memory |

The saved intent keeps the PR URL and review text as workflow inputs. It has one clone step followed by fetch/checkout, project inspection, diff comparison, command checks, review, evaluation and publication, with workspace cleanup in a finalizer. This is the generated structure, not evidence that its external commands or review have succeeded.

Two deterministic contract fixes address the observed host failures:

- The review integration now exports explicit string-enum and strict numeric types. Its output contracts require every property actually serialized, including nullable execution evidence; constructor defaults remain input behavior. Previously these exports could not be reused faithfully as typed workflow ports.
- Flow.Core now retains nested properties, required fields, items and supported constraints when converting JSON Schema type arrays such as `["object", "null"]`. Previously conversion discarded those fields, incorrectly reporting that `null or object` was incompatible with the same typed output contract.

Generic interpretation instructions also explain closed objects, unresolved schema holes and required runtime ports. No model-specific or PR-specific runtime rule was added. Regressions use synthetic contracts and fake integrations, including rejection of incompatible array elements and missing/wrong nested fields.

The offline replay used the last completed intent and its stored catalog with the production graph validation/compiler and isolated fake integrations. Nominal execution, rejected/unavailable confirmation, injected failures, cancellation and both cleanup guards passed. These scenarios establish internal control-flow behavior against fixtures, not live external behavior or complete interpretation of the review instructions. The replay was read-only: it did not save YAML, approval or scenario results to either live session, and did not clear the uncertain request.

| Durable accounting at the stop | Original session | New session |
| --- | --- | --- |
| Session ID | `960846277cd6477ab22d0b434e8cb28f` | `eaa1d8f593f64feeb75487f5f3082429` |
| Revision / status | 26 / stopped | 11 / stopped |
| Calls / current-intent repairs | 7 / 2 | 4 / 2 |
| Accounted input / output tokens | 740,280 / 112,411 | 141,495 / 30,926 |
| Known estimated cost (EUR) | 6.16125 | 1.42431 |
| Active planning time | 955.8 seconds | 278.3 seconds |
| Persisted YAML / approval / scenarios | None | None |

The known combined estimate is **EUR 7.58556**, excluding unknown usage from the new session's fourth call. The original total includes its earlier conservative failed-call estimate. A missing receipt does not establish zero cost. Session counters agree with their durable ledgers; restart replenished nothing.

The remaining live blocker is request reconciliation, not the last candidate's schema. Existing `retry_model` cannot dispatch another repair under the two-repair ceiling. It was not invoked, budgets were not increased and the pending reservation was not discarded. A late receipt can be replayed under its original identity; otherwise an explicit recovery decision is needed before this session can reach persisted final review. Workflow approval and separate publication confirmation remain outstanding.

Local verification after these fixes:

- Full solution suite: **2,411 passed, zero failed, one existing environment-gated external test skipped**, across 29 test projects. This includes 11 host publication tests, 50 planner tests and three new nullable-contract regressions.
- Solution build and Release packages for Flow.Core and Flow.Planning passed with warnings treated as errors and no warnings emitted.
- Published Native AOT planner smoke passed local computation, read/transform and protected writes/cleanup: one fixture interpretation call and zero repairs each, with 1, 3 and 5 scenarios. The publish was warning-free.
- Published trimmed, self-contained Agent.Server persistence smoke passed encrypted schema-6 sessions and review drafts, tenant isolation, revision checks and uncertain-publication replay. The publish was warning-free. This used a fresh temporary store with bundled tools and browser installation excluded; frontend sources were unchanged.
- Restarted the development host with background planning disabled. Read-only API checks confirmed both stopped revisions, all cumulative usage, and the absence of persisted YAML, approval and scenarios remained unchanged. No model request was dispatched during this verification.

## Configuration and first-attempt usage

| Setting or outcome | Observed value |
| --- | --- |
| Host | Local Agent.Server; manual API advances |
| Model | Configured OpenAI `gpt-5.5-2026-04-24` |
| Reasoning | Medium |
| Total call ceiling | 8 per session |
| Repair ceiling | 2 per submitted intent |
| Input/output request limits | 96,000 / 32,768 tokens |
| Calls used | 3: interpretation plus two repairs |
| Input/output usage | 159,216 / 41,100 tokens |
| Estimated cost | EUR 1.76734 |
| Active planning time | 346.5 seconds |
| Final persisted state | Stopped; dependency cycle |

The original 12,000-token input limit rejected the complete catalog before dispatch. The session's request limits were explicitly increased; no capability was silently dropped. The eight-call and two-repair limits were retained. Restart retained cumulative usage.

## Authorized resubmission after the fixes

The exact prompt was read from the encrypted session and sent through Agent.Server's `edit_intent` API command. Comparing the stored prompts before and after confirmed equality. The revision retained cumulative usage and reset only the per-intent repair allowance. Capability discovery completed without a model call.

The next interpretation request was reserved as call 4 of 8. After approximately 301.7 seconds, the configured provider returned HTTP 500. The provider logged one attempt; Agent.Server stopped with `MODEL_DISPATCH_UNVERIFIABLE`. No candidate or completion receipt was returned, so there was nothing to validate or repair.

Read-only verification through public KeyVault APIs confirmed:

- The encrypted request and pending-call reservation remain stored.
- No completion receipt exists for the fourth dispatch.
- Both the session and durable usage ledger retain four calls; the new intent used zero of its two repairs.
- Known cumulative usage remains 159,216 input tokens, 41,100 output tokens, and EUR 1.76734. These figures exclude unknown usage from call 4; the missing receipt does not establish that the failed request was free.
- Active planning time is retained at 655.3 seconds. The session remains stopped at revision 16, without YAML or approval.

The uncertain request was not redispatched and no replacement session was created. The later explicit recovery below retained its reservation and charged conservative estimated usage. Restarts and intent revisions did not erase the reserved call or replenish cumulative budgets.

The existing `UnknownRequestReceipt_IsNotSilentlyDispatchedAgain` regression cases were rerun for transport failure, HTTP 500, and HTTP 503: all three passed. A serialized Agent.Server test-project build with warnings treated as errors completed with zero warnings and errors. Application code was unchanged during this resubmission.

## Explicit provider recovery and remaining failure

Agent.Server now exposes a revision-checked `retry_model` command and a corresponding stopped-session button. A late durable completion is replayed under its original identity. Without a receipt, recovery retains the old dispatch, journals an absolute conservative usage correction through public encrypted KeyVault APIs, and reserves a new identity with the same model request. Automatic restart never invokes recovery. Exhausted budgets prevent a new dispatch, and a crash between the correction and session checkpoint cannot charge the estimate twice.

For the failed fourth dispatch, the correction counted 403,132 input tokens (serialized request bytes) and the complete 32,768-token output allowance. These are conservative budget estimates, not provider-reported usage. The configured call, repair, token, cost, and active-time limits were retained.

| Dispatch or transition | Outcome |
| --- | --- |
| Call 5, explicit retry of interpretation | Provider succeeded; 29 deterministic diagnostics |
| Repair preflight | Duplicated baseline and current intent exceeded the input allowance; no call or repair consumed |
| Call 6, first repair | Provider succeeded; eight dependency/fallback diagnostics remained |
| Call 7, second repair | Provider succeeded; dependency/fallback errors fixed, but an untyped object schema was reintroduced |
| Final deterministic advance | Stopped at revision 26; no extra model call |

Repairs now send the current intent without duplicating the original baseline graph. Input-preflight diagnostics preserve the underlying validation failures; reconfiguration removes the preflight error so validation or repair can resume. Pending requests retain their original interpretation/repair phase after recovery. Generic model instructions clarify nested dependencies, fallback result envelopes, and complete object/array schemas.

The last candidate's check-array item declares `type=object` with neither typed properties nor typed additional properties. Deterministic validation reports `SCHEMA_INVALID` and downstream unavailable/invalid bindings: 13 diagnostics from this root error. No executable holes were reported, but that does not establish validity. YAML compilation, scenario execution, final approval, and PR execution remain blocked. The repair ceiling was enforced without another submission or a fresh session.

Final cumulative accounting: **740,280 input tokens, 112,411 output tokens, EUR 6.16125 estimated cost, and 955.8 seconds active planning time**. These totals include the failed-call estimate; six completed calls themselves reported 337,148 input and 79,643 output tokens. Restart retained revision 26 and these counters. The generation prompt and its PR URL remained unchanged throughout this continuation.

## Offline intent/repair boundary correction

This change addresses planner correctness using a sanitized reproduction and mocked integrations. It does not advance, revise or replenish the stopped live session, invoke the configured model, or execute/publish a pull-request review. Review evaluation and publication enforcement were handled in the separate follow-up below.

The reproduction combines an untyped object inside a check-array schema with a fixture containing a hole. Previously graph validation surfaced the schema failure and its dependent binding errors before checking the fixture. Both independent causes now appear in one pass. Repair context presents the producer schema as the root and names dependent steps; unrelated missing or conditional bindings remain separate. Stored session diagnostics retain all blocking graph findings.

Repair locations are recomputed against the intent by workflow/step identifiers and member names. Regression expectations cover inserted confirmation workflows, reordered steps and arguments, nested branches, missing arguments, fixed catalog arguments and finalizer guards. Generated host defects stop without consuming a model repair. A bounded repair reaches final review using corrected intent coordinates.

One production serializer now supplies compact model-facing intent context and offline test/benchmark responses; the duplicated test formatter is removed. Tests assert explicit locations, keys and outcomes, preserve null versus omission and invalid repair evidence, reject incomplete object/array schemas and nonliteral fixtures, and replay a receipt against its originally reserved response schema. Public planning contracts and schema-6 persistence remain unchanged, as do cumulative budgets, eight total calls, two repairs and approval invalidation.

Verification for this correction:

- Full solution suite: 2,396 passed, zero failed, one existing environment-gated external test skipped. All 50 planner tests passed, including singleton resolution without model calls and the new boundary regressions. Invalid user output schemas remain repairable when their failures propagate through confirmation forwarding; actual host defects still stop.
- Solution build and Release `GnOuGo.Flow.Planning` package creation passed with warnings treated as errors and no warnings emitted.
- Published Native AOT planner smoke passed local computation, read/transform and protected writes/cleanup. Each used one fixture interpretation call and zero repairs; scenario counts were 1, 3 and 5.
- Published trimmed, self-contained Agent.Server persistence smoke passed in a new temporary directory, covering encrypted schema-6 storage, tenant isolation and revision checks. Both publishes were warning-free. As in the reproduction commands below, bundled tools and browser installation were excluded from this persistence check; frontend sources were unchanged and no frontend rebuild was needed.
- Read-only Agent.Server verification: the live session remains stopped at revision 26, with seven calls, two repairs, identical token/cost/active-time accounting, and no YAML, approval or scenarios.

## Offline review evaluation and publication enforcement

The caller-controlled publication helper has been removed. A pure `ReviewEvaluation` computes the review outcome; Agent.Server's `GnOuGo.Review` integration captures original producer results, stores an immutable draft, obtains runtime confirmation, checks the current head and submits the stored content. No new planner phase, semantic proof, model call or schema-6 persistence change was introduced. Planning retains eight total calls and two repairs.

Each declared check appears in the review. Command checks require their own unchanged execution observation, matching declared arguments, observed completion and exit codes in the same absolute working directory. A model's claimed success cannot override an observed failure or replace missing evidence. Incomplete coverage, omitted checks, invalid findings and missing evidence prevent approval. Complete passing reviews can produce `APPROVE` with zero findings; established failed checks or blocking findings produce `REQUEST_CHANGES`; incomplete verification produces `COMMENT`. Duplicate-comment suppression retains the blocking verdict. Empty model output no longer counts as a successful review.

The host captures original Copilot review and execution results at the configured MCP transport boundary and rejects altered results or results from another tenant/execution. Requested-check interpretation and non-execution review evidence remain model judgments, visible for human review. This does not prove exhaustive natural-language interpretation or sandbox arbitrary shell tools.

Publication accepts only a stored draft identifier. Its body, event and target cannot be overridden by a model. Actual Agent.Server confirmation signals expose the exact draft to the UI. After confirmation the host reads the PR head and sends a single review-create request pinned to the reviewed commit. Rejection, abandonment and a changed head prevent the write. Durable reservation precedes dispatch; completed or uncertain attempts replay without another write. GitHub does not provide an atomic head compare-and-swap for this operation, so the boundary is an immediate fresh read plus the pinned reviewed commit.

The configured GitHub MCP integration now exposes declared reads only to generated workflows. All raw writes and unknown effects are filtered from discovery and rejected at dispatch, including new or renamed methods. Review writes use the host publisher. Other mutation workflows require a separately configured integration with its own policy. Encrypted evidence and draft records use public KeyVault abstractions; empty OS lock files serialize publishers sharing the workspace. No SQL configuration access or replacement EF persistence was introduced.

Verification for this follow-up:

- Full solution suite: **2,407 passed, zero failed, one existing environment-gated external test skipped**, across 29 test projects. Ten host publication regressions cover original evidence, tenant/execution isolation, actual confirmation channels/signals, stale heads, rejection/abandonment, cancellation, concurrent attempts, uncertain dispatch and completed replay. Core tests cover derived check outcomes, reused/missing observations, incomplete reviews and suppressed blocking findings.
- Solution build, Release `GnOuGo.GithubCopilot.Core` package creation, and both Native AOT publishes completed with warnings treated as errors and no warnings emitted.
- Published Native AOT planner smoke passed local computation, read/transform, and protected writes/cleanup: one fixture interpretation call and zero repairs each, with 1, 3 and 5 scenarios. No planner production files changed.
- Published Native AOT Copilot MCP stdio discovery exposed 41 tools, excluded the deleted publication helper, and exported review completeness and blocking-finding counts. No tool or model was dispatched by this smoke.
- Published trimmed Agent.Server persistence smoke passed encrypted schema-6 storage plus review drafts, tenant isolation and uncertain-publication replay. Published review tool schemas also validated. This ran in a fresh temporary store; bundled tools/browser installation and frontend rebuilding were excluded. Frontend sources were unchanged.
- Read-only verification through Agent.Server confirmed the live session remains stopped at revision 26, seven calls and two repairs, with identical cumulative token/cost/active-time accounting and no YAML, approval or scenarios. No live model call, PR clone, review execution, GitHub publication, merge or deployment ran for this follow-up.

The opt-in external Copilot E2E fixture now evaluates the reviewer without publishing a review. It was not run for this change. Live end-to-end generation and execution with the original prompt remain unverified; the stopped session was not advanced.

## Earlier changes derived from the live diagnostics

- Align catalog prompt schema names with supported `/input` and `/output` references. Explain input-port references, integration payloads, confirmation results, and self-reference restrictions in the typed interpretation prompt.
- Permit guarded finalizer dependencies on completed main steps. Cleanup remains unavailable if its producer was skipped or failed.
- Report independent contract errors alongside unresolved holes and scope errors. Cycle diagnostics identify the step and dependency chain.
- Reserve repair attempts together with actual model requests. Input preflight failures consume neither calls nor repairs. Reaching the repair ceiling no longer reports a fictitious unchanged model response.
- The initial publication helper derived `APPROVE`, `REQUEST_CHANGES` and `COMMENT` from supplied results. The host-owned publication follow-up above replaces this helper and removes its caller-controlled confirmation and `auto_comment` paths.

These changes have deterministic regression coverage. They do not establish success for the stopped live session. The separate opt-in external Copilot E2E fixture is not evidence of this Agent.Server flow.

## Local verification after the fixes

- Full solution suite after recovery changes: 2,375 passed, zero failed, one environment-gated external test skipped. This includes 29 planner, 89 Copilot Core, 110 Copilot MCP, and 303 Agent.Server tests. Recovery tests cover explicit retries, missing receipts, restart between correction and checkpoint, stale revisions, session ownership, and exhausted call/repair/token/cost limits.
- Solution build with warnings treated as errors: zero warnings or errors.
- Release packages created for the changed `GnOuGo.Flow.Core` and `GnOuGo.Flow.Planning` libraries; the earlier publication-gate follow-up also packaged `GnOuGo.GithubCopilot.Core`.
- Published Native AOT planner smoke: local computation, read/transform, and protected writes/cleanup passed. Each used one fixture interpretation call and zero repairs; isolated scenario counts were 1, 3, and 5 respectively.
- Published trimmed, self-contained Agent.Server persistence smoke passed encrypted schema-6 storage, tenant isolation, and stale-revision rejection. Bundled tools, browser installation, and frontend rebuilding were excluded from this persistence check.
- Published Native AOT Copilot MCP passed synthetic publication-gate calls over stdio for zero findings, blocking findings, missing execution evidence, rejected confirmation, and stale heads. This exercised the exported contract without model calls or external effects.
- Restarted the actual development host after rebuilding. The stopped live session retained its exact revision, calls, repairs, tokens, cost, and active time. The Blazor boot script returned HTTP 200. Agent.Server's Vite frontend build passed without warnings for the recovery follow-up; the new retry button was also compiled by the Blazor build.

Reproduction commands (macOS arm64, .NET SDK 10.0.300):

```bash
dotnet test GnOuGo.Agent.sln --no-restore -m:1 --verbosity quiet -warnaserror -p:SkipClientBuild=true
dotnet build GnOuGo.Agent.sln --no-restore -m:1 -warnaserror -p:SkipClientBuild=true
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.GithubCopilot.Core -c Release -warnaserror
dotnet publish src/GnOuGo.GithubCopilot.Mcp -c Release -r osx-arm64 --self-contained true \
  -warnaserror -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false \
  -p:SkipModelMetadataGeneration=true
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true \
  -warnaserror -p:PublishTrimmed=true -p:PublishSingleFile=true -p:PublishAot=false \
  -p:SkipClientBuild=true -p:SkipBundledServerTools=true -p:SkipPlaywrightBrowserInstall=true
planning_smoke_dir=$(mktemp -d /tmp/gnougo-planning-persistence.XXXXXX)
src/GnOuGo.Agent.Server/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Agent.Server \
  --planning-persistence-smoke "$planning_smoke_dir"
```
