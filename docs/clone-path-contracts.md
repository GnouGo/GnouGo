# Clone-path contracts and cleanup regression

This change continues `feat/flow-hybrid-planning-v9` from `ad3e896`, tracked by [issue #112](https://github.com/GnouGo/GnouGo/issues/112) and [draft PR #113](https://github.com/GnouGo/GnouGo/pull/113). It preserves the TaskPlan architecture, public Flow DTOs, planning format 10 and execution journal schema 9.

## Diagnosis and correction

The retained CodeuSerieux execution sent `targetDirectory: "SmartGuide"` to Git while cleanup independently used `workflows/github-pr-review`. Git rejected the clone. The old published target schema was an unrestricted string, so planning had accepted the binding. Cleanup completion only established absence of the other directory.

Git now publishes and enforces one producer-owned, portable string pattern: a relative `workflows/` path with nonempty slash-separated child segments. Root-only, absolute/drive/UNC/home/URI paths, backslashes, dot/parent segments, wildcards and control characters are rejected. No prefixing, trimming or automatic rewriting occurs. **Absolute clone targets are no longer accepted**, even inside the workspace. Existing runtime checks for confinement, reserved paths, files, nonempty destinations and permissions remain authoritative. Syntax does not prove filesystem availability or ownership.

Tool and parameter descriptions explain that clone may create an absent target, needs no preliminary directory creation, and may leave partial content on failure. Keep the original declared location for cleanup; use successful `projectRootRelative` for subsequent repository operations. Policy inspection is optional. Cmd's configuration, scripts, allowlist, deletion semantics and timeouts are unchanged.

Flow uses the existing generic compatibility checks. Diagnostics now show declared string `pattern`, `minLength` and `maxLength` with the semantic consumer and known producer location. A regression exposed that a named literal lost its constant schema during compilation. The compiler now preserves explicitly declared string constants through business bindings/captures; overridable defaults and transform outputs remain broad. No path inference, new type constraint, provider-name rule, phase or IR was added.

Generation guidance asks for one explicit resource location reused by creation and cleanup. The synthetic correction removes location generation from the transform and assembles cleanup arguments using existing object and `json` values. The original failed plan is rejected at `/tasks/clone_repository/inputs/targetDirectory`; its one-slot repair permissions do not authorize inserting the shared-location task or rewriting unrelated tasks.

## Evidence and limitations

[Regression fixtures](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ClonePlanning/README.md) retain sanitized responses, original issued schemas, exact discovery contracts and relevant execution receipts. Original encrypted sessions, executions and accounting remain unchanged. Historical and synthetic fixtures are explicitly distinguished.

The real planner replays the original responses, including recovery. Actual updated Git discovery changes the capability fingerprint and invalidates previous approval material. Renamed generic capabilities exercise the same compatibility rules. Local execution uses actual Git operations against a temporary repository and actual Cmd cleanup, with mocked inference/review effects. Independent assertions check the single clone destination, returned repository path, unchanged source/unrelated directory and cleanup after success, absent/partial creation, failure, cancellation and refusal.

**The complete review workflow is not proven successful.** Its replay exposed a separate existing runtime problem: `McpCallExecutor` strips progress arrays from tool responses, including `progressEvents` required by the producer schema. Typed JSON assembly subsequently fails with `$.value.checks.progressEvents: missing required property`. The full recording and exact failure are retained; the successful lifecycle test is a clearly identified subset. Runtime behavior is unchanged by this clone-path pass. The existing real Copilot sandbox/command-execution limitation also remains; mocked effects do not resolve it.

No paid inference, live benchmark, external repository workflow or real Copilot task was executed.

## Validation

Final deterministic and branch CI results are recorded in PR #113. Local logs, including unsuccessful intermediate checks, are retained under `artifacts/clone-contracts-2026-09-27/`. Git's actual stdio schema is checked against runtime path cases with both .NET and JavaScript Unicode regex parsers, including the Native AOT executable. Existing filesystem/policy checks remain covered by the Git suite. Package and planner Native AOT checks preserve all eight deterministic business scenarios.

The preceding revision had a Windows x64 desktop Cmd timeout failure; it is not reclassified as a pass or bypassed here. Release-only skipped publishing jobs are distinct from required validation.

## Deployment and recovery

Deploy the updated bundled Git MCP and planner together. Refresh capability discovery, then regenerate CodeuSerieux or explicitly revise its semantic plan and approve the new artifact. Share one workflow-owned creation location with cleanup, without depending on clone success. No saved workflow, configuration, approval, failed run or exhausted budget is rewritten or resumed automatically. Keep the PR draft and do not merge while validation or execution limitations remain unresolved.
