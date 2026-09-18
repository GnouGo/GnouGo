# Agent.Server live planning validation — 2026-09-18

## Result

The schema-6 refactor was committed and pushed as `ff567a2` on `feat/deterministic-planner-v2`. The user's unchanged French PR-review prompt was submitted through the actual Agent.Server planning API using its configured model and KeyVault-backed integrations.

The live attempts **did not reach final review or execution**. Deterministic validation rejected the first generated graph. Two repairs were exhausted; a step still referenced its own unfinished result. After fixes in `022f0db`, the user authorized resubmitting the exact prompt. That interpretation request failed at the provider with HTTP 500 before returning a result. No generated YAML was substituted by hand, and no PR-specific planning path was added.

No repository clone, dependency installation, project test execution, or GitHub publication occurred. Consequently this run provides no evidence that the target pull request passes any requested check. Workflow approval, runtime publication confirmation, and a fresh head check remain outstanding. Private repository content, credentials, model responses, and raw host logs are excluded from this report.

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

The uncertain request was not redispatched and no replacement session was created. Continuing requires resolving the provider failure and the missing completion/usage evidence. Restarts and intent revisions must not erase the reserved call or replenish cumulative budgets.

The existing `UnknownRequestReceipt_IsNotSilentlyDispatchedAgain` regression cases were rerun for transport failure, HTTP 500, and HTTP 503: all three passed. A serialized Agent.Server test-project build with warnings treated as errors completed with zero warnings and errors. Application code was unchanged during this resubmission.

## Changes derived from the live diagnostics

- Align catalog prompt schema names with supported `/input` and `/output` references. Explain input-port references, integration payloads, confirmation results, and self-reference restrictions in the typed interpretation prompt.
- Permit guarded finalizer dependencies on completed main steps. Cleanup remains unavailable if its producer was skipped or failed.
- Report independent contract errors alongside unresolved holes and scope errors. Cycle diagnostics identify the step and dependency chain.
- Reserve repair attempts together with actual model requests. Input preflight failures consume neither calls nor repairs. Reaching the repair ceiling no longer reports a fictitious unchanged model response.
- Extend the existing publication gate to derive `APPROVE` for complete passing reviews, including zero findings; `REQUEST_CHANGES` for blocking findings or established failed checks; and `COMMENT` for incomplete verification. Required command checks need original completion observations, working directories, and exit codes. Missing or conflicting execution observations cannot establish success. Interactive publication still requires confirmation, and `auto_comment` remains comment-only.

These changes have deterministic regression coverage. They do not establish success for the stopped live session. The separate opt-in external Copilot E2E fixture is not evidence of this Agent.Server flow.

## Local verification after the fixes

- Full solution suite: 2,364 passed, zero failed, one environment-gated external test skipped. This includes 26 planner, 89 Copilot Core, 110 Copilot MCP, and 295 Agent.Server tests.
- Solution build with warnings treated as errors: zero warnings or errors.
- Release packages created for the changed `GnOuGo.Flow.Planning` and `GnOuGo.GithubCopilot.Core` libraries.
- Published Native AOT planner smoke: local computation, read/transform, and protected writes/cleanup passed. Each used one fixture interpretation call and zero repairs; isolated scenario counts were 1, 3, and 5 respectively.
- Published trimmed, self-contained Agent.Server persistence smoke passed encrypted schema-6 storage, tenant isolation, and stale-revision rejection. Bundled tools, browser installation, and frontend rebuilding were excluded from this persistence check.
- Published Native AOT Copilot MCP passed synthetic publication-gate calls over stdio for zero findings, blocking findings, missing execution evidence, rejected confirmation, and stale heads. This exercised the exported contract without model calls or external effects.
- Restarted the actual development host after rebuilding. The stopped live session retained its exact revision, calls, repairs, tokens, cost, and active time. The Blazor boot script returned HTTP 200. Frontend sources were unchanged in this follow-up; both frontend builds had passed for the initial refactor.

Reproduction commands (macOS arm64, .NET SDK 10.0.300):

```bash
dotnet test GnOuGo.Agent.sln --no-restore --verbosity quiet
dotnet build GnOuGo.Agent.sln --no-restore -warnaserror -p:SkipClientBuild=true
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
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
