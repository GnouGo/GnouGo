# Desktop planning protocol diagnosis — 2026-09-20

## Result and scope

The native Desktop test reached `workflow.plan`, discovered 129 capabilities, and stopped
before producing an intent. Authentication succeeded; the configured gateway returned
HTTP 404 to the Responses endpoint. The host reported this as “The requested LLM model
is unavailable.” This message alone does not establish that the model is unavailable
through Chat Completions.

The isolated Desktop test was restarted with the existing provider setting
`LLM:Models:OpenAi:RequestPolicy:BackgroundProtocol=ChatCompletions`. This selects the same
protocol used by the successful live benchmark. No planner or provider implementation,
workflow, model, credential, public API, storage format or retry policy changed. A new
real Desktop request is still required to verify generation with this configuration.
No review execution or publication has succeeded yet.

Starting source: `0c9a6d4b4ebb173f420c02b4a44d3765635821a7`, branch
`feat/deterministic-planner-v2`. Planner behavior remains frozen at `65dc34a`.
The user explicitly waived the earlier provider-budget verification prerequisite.
EUR 20 is now a best-effort additional-spending target, not a verified provider ceiling;
unknown Copilot usage must remain unknown. All other execution and approval gates remain.
Previous benchmark campaigns and stopped sessions are retained.

## Real Desktop preflight

- The Agent frontend installed from its frozen lockfile and built successfully. Desktop
  built in Release with zero warnings and errors. Photino created the native window;
  embedded `page-loaded` and `client-ready` callbacks arrived. External-browser fallback
  was disabled. The host used dedicated Agent, planning and telemetry database paths,
  existing KeyVault credentials, and disabled automatic planning background processing.
- The unchanged `workflow-planning.yaml` example was imported through normal agent
  management as `desktop-planning-e2e`. Its only workflow steps are `workflow.plan` and
  `workflow.execute`. The user selected it and submitted the real request in Desktop.
- Real MCP discovery connected to GitHub (48 tools), Git (22), Cmd (3), and Copilot (41).
  GitHub MCP authenticated as `guillaume-chervet`. A read with that same credential
  returned repository push permission; no publication was attempted. Git/Cmd/Code policy
  reads succeeded. No Copilot inference or review repository command ran.
- The selected candidate was [SmartGuide PR #597](https://github.com/AxaFrance/SmartGuide/pull/597),
  open, non-draft, authored by `gfortaine`, with no auto-merge request at inspection.
  Base: `667b097d205f7a376e7c8fc09f1f88e08a4dc5c6`;
  head: `0b7899b1944aaab5b742e12c6238825155fa2bac`.
  It changes two API files. Inspected repository workflows had no review-triggered
  deployment; suitability and head must be checked again before any publication run.

## Durable failure evidence

Session: `1547795a624aedfeb68198e8649ef2519c3e6a637f72ad02e71430a5e48ac332`.
Tenant: `default`. Status: `stopped`, revision 2, diagnostic
`MODEL_DISPATCH_UNVERIFIABLE` at `$`. Created at `2026-09-20T11:44:17.137789Z`,
updated at `2026-09-20T11:44:22.172696Z`; recorded active time approximately 5.024 seconds.

| Observation | Result |
| --- | --- |
| Configured model / reasoning | `OpenAi` / `gpt-5.5-2026-04-24` / medium |
| Request limits | 12,000 input tokens, 8,192 output tokens; eight calls, two repairs |
| Calls / repairs | 1 / 0 |
| Reserved model requests / completion receipts | 1 / 0 |
| Request mode | `useBackgroundMode=true`, `disableTransportRetries=true` |
| Intent / graph / YAML / scenarios | None produced |
| Verified inference tokens / cost | Unknown; zero counters without a receipt are not evidence of zero usage |
| Workflow execution / clone / cleanup | Execution never started; no review workspace was created |

The public encrypted KeyVault record API was used for inspection; no direct SQL or
credential dumping was involved. Reservation content SHA-256:
`7028b7c20d72ab3bee4f6cc4cf10fa8ed546d395f32bfbb3e02556b7d5fb34ff`.
Budget-record content SHA-256:
`957531d2c1f7a23d4955114fc8957bd5870bb2326517aa5f4d290b2873326f37`.
The reservation, original schema, session and budget were not rewritten or resent.

The public telemetry API exposes trace `235eb040a02bbc0f21c91f19dcacb7fe`:

1. OIDC discovery: HTTP 200.
2. Authentication token exchange: HTTP 200.
3. Provider `POST .../responses`: HTTP 404, approximately 573 ms.

Private hostnames, authentication material and request content are omitted here.
Classification: **provider/transport failure, with a protocol-configuration mismatch as
the supported diagnosis**, not a demonstrated planner/model interpretation defect.
The benchmark dispatches a synchronous copy through Chat Completions; Desktop honored
the planner's background request and the default `Auto` protocol, which selects Responses.
There is intentionally no automatic fallback following a non-transient HTTP 404.

## Correction and offline checks

The correction is an isolated launch setting, documented in the
[Agent.Server README](../src/GnOuGo.Agent.Server/README.md#provider-http-retries).
The existing provider regression now also covers `/v1` and deployment-style endpoints,
asserting one Chat Completions request and no Responses request. Existing tests continue
to reject automatic fallback after a 404. KeyVault-overlay tests preserve the selected
request policy while resolving credentials. An in-memory effective-options probe
reported `ChatCompletions` with zero inference calls.

Commands and results:

```text
corepack pnpm install --frozen-lockfile                    PASS (Agent.Server/ClientApp)
corepack pnpm build                                       PASS (Agent.Server/ClientApp)
dotnet build src/GnOuGo.Agent.Desktop -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
                                                         PASS, 0 warnings / 0 errors
dotnet test tests/GnOuGo.AI.Core.Tests -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
                                                         PASS, 222 tests
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true --filter 'FullyQualifiedName~KeyVaultRuntimeConfigStoreTests|FullyQualifiedName~SecureWorkflowRuntimeFactoryTests'
                                                         PASS, 14 tests
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
                                                         PASS, 147 tests
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
                                                         PASS, 0 warnings / 0 errors
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -c Release --
                                                         PASS, 8 cases / 31 execution variants
```

The focused provider tests passed before extending their endpoint coverage. No production
source fix was necessary. All offline execution variants passed with zero safety violations;
these fixtures make no live reliability claim. The first benchmark launch used an implicit
parallel build and hit a static-web-assets compression file lock. The explicit serial build
and subsequent `--no-build` run passed without source changes or warning suppression.
Full solution/release publishing checks were not repeated for
this test/documentation change; the [release report](planning-finalization-2026-09-20.md)
remains separate historical evidence.

## Remaining execution requirements

The restarted native window reported page-loaded/client-ready and the host health check
returned 200. The failed session remains stopped. A fresh user submission through Desktop
must use a new execution/request identity; do not use the failure's workflow-revision offer
to alter the generic bootstrap. Inspect the generated artifact before workflow approval.
The configuration correction is not yet a successful live-generation result.

Restart reused the same isolated databases. Graceful termination stopped the embedded host;
the remaining native shell then needed process termination. No review execution was active.
After restart, the original reservation and budget hashes above, session revision/timestamps,
one-call count and absence of receipts were unchanged. No automatic request was dispatched.

The complete real test still requires three read-only reviews and one additional fresh,
separately confirmed publication execution. Real dependency/check evidence, unchanged
tracked source, cleanup, an immutable evaluated review and a fresh publication head check
remain unverified. No SmartGuide source edits, commits, pushes, metadata changes, reviews,
merges or deployments occurred in this attempt.

An incidental read-only diagnostic query found that telemetry date-range filtering returns
an EF translation error. Unfiltered trace discovery and trace-by-ID inspection worked and
provided the evidence above. This did not cause the planning failure and was not changed
as part of the protocol correction.
