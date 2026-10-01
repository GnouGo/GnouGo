# Planning clarification and caller inputs

The saved GithubLoveru1 Designer session was interactive, with no `PlanningChoice` entries. It required seven caller inputs although the user expected two. Previously, the planner could ask a business question only after a complete TaskPlan passed compilation. There was no early question response or custom-answer path.

The [sanitized input-interface excerpt](../tests/GnOuGo.Flow.Planning.Tests/Fixtures/Clarification/retained-input-interface.json) preserves that finding. It is not a complete workflow replay. The prompt did not identify the intended two fields, so tests use explicitly synthetic answers and arbitrary field names. The saved workflow and approvals are untouched.

## Behavior

- Clear requests can reach review in one planning call with no questions or repairs.
- Material uncertainty about caller inputs, missing facts or business alternatives can produce one to three questions instead of a plan/discovery/repair response. Alternatives include tradeoffs and exactly one recommendation; missing facts can use a text-only question.
- Designer and originating chat share a question component with recommended selections, a custom-answer textarea and explicit submission/cancellation. Auto mode also pauses for material clarification. Switching modes cannot answer it.
- Generic `workflow.plan` hosts use existing human-input forms. Hosts without an interaction provider return a waiting session ID and revision.
- Declared literal choices still compile locally without another model call. A custom answer requests an intent revision and retains the previous plan/requirements as context; it is never an unchecked binding.
- Questions and answers are encrypted with the session. Continuation happens after the answer checkpoint and preserves call, token, repair and active-time budgets. Human wait is measured separately. A final-call question may be answered and retained, but continuation stops at the existing ceiling.
- Clarification does not grant permissions or approval. Final approval still identifies the exact validated artifact.

## Contracts and recovery

New proposals add nullable `clarifications`, exclusive with `plan` and `discoveryRequests`; repair envelope v3 offers the same exclusive action alongside patches/discovery. Questions carry `id`, `question`, `alternatives` (`id`, `description`) and nullable `recommended`. Two or three alternatives require a matching recommended ID; text-only questions have no alternatives or recommendation.

`PlanningCommand` and its HTTP DTO support `kind: "answer"` and `answers`. Each entry contains `questionId` and exactly one of `alternativeId` or nonblank `text` (maximum 8,192 characters). A batch must answer exactly the pending question IDs. Duplicate, stale, missing or unknown answers reject atomically. Both Designer and chat transport retain optimistic revisions and tenant/origin restrictions. Chat transport cannot approve or execute.

`PlanningRequirements.Inputs` reuses `TaskInput`. Null means unresolved during discovery or historical semantics; an empty array means no caller inputs. New sessions require a concrete interface before a plan. The host rejects changed names, types, requiredness or defaults before compilation and again at approval. An explicit revision is required to change accepted intent. Authoritative producer contracts are still required to derive technical values; answers cannot manufacture typed metadata fields.

`intentVersion`, `pendingQuestions` and `answerHistory` are optional format-10 session additions, omitted when absent. Existing TaskPlan/PlanningGraph formats and approval material are preserved. Older pending requests replay their original schemas; v1/v2 repair fingerprints use their original definition templates. V3 also binds requirements and clarification permissions. Response-schema definition names are compacted only for newly issued requests; constraints remain unchanged.

## Validation

The [sanitized validation record](evidence/planning-clarification/validation.json) reports **3,892 passed, zero failed and 12 existing skips across 33 projects**, with `-warnaserror` and the browser test enabled. The unchanged baseline had 3,846 passed and the same 12 skips. Planner tests: 719; Agent.Server: 545; Flow integrations: 85.

Four deterministic interaction paths execute real `workflow.plan` followed by separately approved `workflow.execute`; all eight output observations, including restart, equal 42. The clear-request case retains one planning call and zero repairs; a scripted ambiguous request uses one question batch, two calls and zero repairs. These are deterministic regression results, not new live benchmark measurements. Frontend production build, three Release packages, Native AOT planning, trimmed-server encrypted persistence and Chromium interaction checks pass.

Run the deterministic suites and builds from the repository root:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
pnpm --dir src/GnOuGo.Agent.Server/ClientApp run build
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

The browser test starts a disposable host using the actual Designer, encrypted persistence and a deterministic model. It checks explicit recommendation/custom submission, text-only input, reload, keyboard interaction, mobile width and cancellation:

```sh
browser_dir=$(mktemp -d)
pnpm --dir "$browser_dir" add playwright@1.63.0
pnpm --dir "$browser_dir" exec playwright install chromium
PLAYWRIGHT_MODULE_PATH="$browser_dir/node_modules/playwright/index.mjs" \
  dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter FullyQualifiedName~PlanningClarificationBrowserTests
```

Without that variable the browser-only test explicitly skips. Screenshots and browser results go to `artifacts/planning-clarification/browser`. Component and encrypted HTTP recovery tests run normally. `PlanningClarificationExecutionTests` run actual `workflow.plan` and `workflow.execute` with deterministic adapters through both interaction paths and independently assert the numeric result, separate approval and no repeated model dispatch after restart.

Publish sequentially when sharing project output directories:

```sh
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror \
  -o artifacts/planning-clarification/published-server \
  -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true \
  -p:SkipClientBuild=true -p:SkipBundledServerTools=true \
  -p:SkipModelMetadataGeneration=true -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false
artifacts/planning-clarification/published-server/GnOuGo.Agent.Server \
  --planning-persistence-smoke "$(mktemp -d)"
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Agent.Shared -c Release -m:1 -warnaserror
```

The published smoke covers encrypted questions, answers, caller contracts and tenant isolation. Bundled MCP tools are skipped because this change does not modify them; the frontend is built separately above. This correction makes no paid provider calls, executes no external repository and does not claim the model will identify every natural-language ambiguity. The historical 33/33 execution campaign is unchanged and is not evidence for this feature.
