# .NET dependency refresh — 2026-11-07

All 59 direct NuGet package IDs were checked against the official registry, including conditional platform references. The refresh updates 51 IDs across 238 references in 63 projects; the other eight IDs were already current. [Exact versions](versions.json) retain the old/new values and registry sources. The .NET 10 target and existing SDK policy are unchanged.

| Group | Updated versions |
| --- | --- |
| Microsoft extensions, EF Core, SQLite, ASP.NET testing | 10.0.12 → 10.0.14 |
| MCP packages | 2.2.0 → 2.4.0 |
| GitHub Copilot SDK | 1.0.14 → 1.0.29; matching SDK-pinned CLI 1.0.107 |
| LLamaSharp and CPU backend | 0.27.0 → 0.30.0 |
| Jint | 4.16.3 → 4.17.0 |
| OpenTelemetry SDK/API/exporter | 1.19.1 → 1.20.1 |
| OpenTelemetry instrumentation | 1.19.0 → 1.20.2 |
| SkiaSharp, including Linux native assets | 4.152.1 → 4.155.0 |
| Playwright | 1.62.0 → 1.64.1 |

Additional tokenizer, ONNX, renderer, Markdown, gRPC, CLI and test dependency updates are listed individually in the version manifest. Native backend imports and capability-report versions match the new packages. The existing permission/capability allowlist is preserved; new experimental SDK capabilities are not enabled. Existing database schemas, encrypted records, journal formats and financial authorizations are not migrated by this refresh. Historical qualification evidence remains unchanged.

## Validation

Validation uses .NET SDK 10.0.403 and macOS ARM64. The complete unfiltered run on frozen dependency-update source `f015f5a4bad6feba552cd743ed54e447a41a0e81` passed **8,197 tests, 13 existing skips and zero failures**, across 33 assemblies with no compiler diagnostics and unchanged tracked source. [Exact full results](full-solution-validation.json) retain that identity. [Recorded checks and artifact hashes](validation.json) also link the separately tested follow-up below.

| Check | Result |
| --- | --- |
| Complete unfiltered solution tests on the dependency commit | 8,197 passed; 13 existing skips; zero failures |
| Complete solution build with `-warnaserror` | Passed; zero warnings/errors |
| Outdated direct packages | No findings; conditional platform references also checked directly against NuGet |
| Vulnerable/deprecated packages, including transitive dependencies | No findings across 77 projects |
| Explicitly packable library projects, Release with `-warnaserror` | 15 passed |
| Existing host publish profiles, plus Proxy and Planning smoke | 16 passed: 11 Native AOT, five trimmed managed hosts |
| Native Flow CLI/Jint triage | Passed with Jint 4.17.0 |
| Unsuppressed native warning audit, Flow CLI and Files | Exact existing allowlists match: 16 and 13 origins, no additions |
| Published Flow CLI/server/Copilot MCP checks | Passed: encrypted receipts, index rebuild, restart, revisions, tenant isolation and bounded MCP refusal |
| Published Files Native AOT | Upload/download, tenant isolation, restart and TTL purge passed |
| Published Animation, Agent MCP, KeyVault MCP and OTLP | Representative YAML validation and startup/health checks passed |
| Published Agent.Server | Normal startup, root UI and Blazor negotiation passed |
| Encrypted cross-version storage | Created with the previously published EF/SQLite 10.0.12 build, reopened by 10.0.14 in two separate processes; exact receipts reused |
| Published Desktop assets | Five bundled MCP executables, Blazor script and CPU/Metal libraries present |
| Published Proxy Native AOT | Four controlled providers, native streaming/tool round trips, OIDC, UI and unknown-usage accounting passed |
| Planning Native AOT smoke | Passed with the unchanged deterministic planning/repair assertions |
| Copilot Native AOT SDK/runtime handshake | Native `copilot_connectivity` and `copilot_status` passed; runtime reports 1.0.107, protocol 3; no conversation or inference |
| Real embedded Qwen3 structured output | One explicitly enabled inference test passed with LLamaSharp 0.30.0 |

CI exposed a pre-existing timer/UTC-clock race in background receipt recovery: the generation timer could expire while UTC resolution still reported positive remaining time, allowing an unwanted retrieval GET and obscuring the timeout. A deterministic frozen-clock regression failed before the correction. Commit `458c2f4ea1c8e8b321e8e9ae3f4768429ee4b3f5` makes recorded timer expiry authoritative for that invocation, while a separate cold recovery may still retrieve the original response. No deadline, retry allowance, route, reservation or replay protection is relaxed.

The [follow-up validation](followup-validation.json) passed all 32 non-Server assemblies (**6,413 passed, 12 existing skips**), all **226 targeted Server transport/journal/retention/budget tests**, and the full solution build with zero warnings/errors. These include all 354 AI.Core tests and the unchanged 150 ms cold-recovery regression. AI.Core was repackaged; Proxy/Planning Native AOT and trimmed Server were republished without diagnostics, with repeated published streaming and encrypted-recovery smokes. The complete unfiltered solution suite was not rerun after this narrow correction; its earlier result is not relabeled as testing the later fix.

[CI evidence](ci-validation.json) records the corrected Linux standalone job passing and extracted Proxy Native AOT release checks passing on all six Windows/Linux/macOS targets. This also includes the Proxy frontend build and tests, separately from the local .NET validation. The broader planner CI workflow was still running when recorded.

Publishing follows the repository's existing profiles and narrow dependency exceptions. The two already-audited EF Tasks experimental notices remain visible; there are no unexpected publish diagnostics and no added suppressions. The PowerShell orchestration was unavailable locally; its 14 publish profiles were executed directly, and its runtime assertions were exercised through the existing Python checks and equivalent isolated HTTP checks.

The real local-model test used the existing pinned Qwen3 artifact, 428,970,080 bytes, SHA-256 `da2572f16c06133561ce56accaa822216f2391ef4d37fba427801cd6736417d4`. The ordinary opt-out test-suite entry is not presented as proof of native inference.

The full test command runs without a filter in an unchanged detached checkout:

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror \
  -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true \
  --logger trx --results-directory ../dotnet-dependency-update-20261107/full-tests
```

Cross-version storage uses only a new isolated fixture and the existing `--planning-persistence-smoke` entrypoint. The older published source is `702ba2be8f1f76e7363020dc505cca43ea209491`. Its durable native HTTP receipt is recovered by both new processes without another dispatch; the fixture also exercises normal new writes. No production database or financial campaign is modified.

Frontend packages were not changed or re-audited; their separate TypeScript checks/build were not rerun locally. Proxy CI supplies the frontend and cross-platform checks described above; other components were exercised locally on macOS ARM64. The Desktop graphical window was not launched. Provider account inference was not performed; Copilot handshake and proxy protocol checks do not establish paid-provider availability. No paid-provider inference was required for this dependency update.

## Primary references

- [NuGet V3 registry](https://api.nuget.org/v3/index.json).
- [LLamaSharp 0.30 release](https://github.com/SciSharp/LLamaSharp/releases/tag/v0.30.0) and [pinned native iterator](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/LLama/LLamaStatelessExecutor.cs).
- [Jint 4.17 release](https://github.com/sebastienros/jint/releases/tag/v4.17.0).
- [MCP 2.4 release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.4.0): OAuth and transport hardening; experimental Skills remain a separate opt-in package.
- [Copilot SDK 1.0.29](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.29); its packaged build props pin CLI 1.0.107.
