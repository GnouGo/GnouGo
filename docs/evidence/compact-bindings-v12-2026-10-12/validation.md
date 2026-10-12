# Validation provenance

Core/runtime source: `a09f8407` (Gate A/B/C plus nested-output correspondence). The final compiler follow-up adds conservative identity-observer guards and four equivalence cases; all 55 fusion cases pass. Documentation/evidence follow-ups do not change binaries. Final whole-solution counts and current-head CI verdicts are recorded in [PR #117](https://github.com/GnouGo/GnouGo/pull/117); consult the [candidate checks](https://github.com/GnouGo/GnouGo/pull/117/checks) rather than a result from an earlier profile.

## Deterministic gates

- Gate A: 49 Flow and 87 Planning focused cases passed.
- Gate B: 64 Flow and 51 Planning focused cases passed.
- Gate C: 109 initial Flow cases, 66 expanded telemetry/recovery/output cases and three host compiler/streaming cases passed.
- Final correspondence/trace review: 96 Flow cases passed with `-warnaserror`.
- Complete Planning suite before the final output-only preflight tightening: 1,290 passed, zero skips/failures. The full-solution run includes it again on the final source.
- Local actual Browser/Document comparison: 16 passed, eight per profile. Exact workbook and negative-oracle results are retained in `local-execution.txt`.
- Planning skill passes `skill-creator/scripts/quick_validate.py`.

## Full solution and CI

The source candidate is checked in an isolated worktree with:

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror \
  --logger 'trx;LogFilePrefix=validated' \
  --results-directory /tmp/gnougo-v12-validated-results
```

The console log is `/tmp/gnougo-v12-validated.log`. Full candidate CI includes platform packages, host tests, deterministic planning, Native AOT and recovery. A non-blocking job is inspected for failed steps; a green parent job alone is insufficient. No skipped or superseded job is represented as a pass.

An earlier clean build exposed `ConfigureProvidersServiceTests.ExecuteAsync_LlmDefault_PersistsDefaultLlmViaMountedAgentMcp` inheriting collector appsettings and binding fixed port 4317, occupied by an existing local container. The test now explicitly binds the inherited gRPC endpoint to ephemeral loopback, as well as configuring its HTTP endpoint. It still checks persisted provider/model configuration, and now asserts loopback addresses. Production configuration and the running container were untouched. The focused regression passed. Superseded full runs were cancelled after retaining their logs so the final source could run from the start; no timeout or assertion was weakened.

The initial parallel host build also encountered the known generated-static-asset collision. Serial `-m:1` builds passed. The package/publish/test commands use serial builds within each checkout.

## Packages, AOT and encrypted recovery

The five independently publishable Flow packages (Core, Planning, Integrations, Copilot, Persistence) were packed with `-c Release -m:1 -warnaserror` into `/tmp/gnougo-v12-packages`.

Planning smoke was published for `osx-arm64 --self-contained true -warnaserror` and executed natively. It explicitly tests checked outputs, ordered aliases, exact numeric/null values, source-generated serialization and the existing planning/mapping corpus. Logs: `/tmp/gnougo-v12-aot-validated.log` and `-run.log`.

Flow CLI and Server were published with the existing `verify-warning-free-publishes.ps1` Native AOT profile: `PublishAot=true`, `PublishTrimmed=true`, `PublishSingleFile=true`, `SkipClientBuild=true`, `SkipModelMetadataGeneration=true`. Each log contains exactly the two already-audited EF Core Tasks 10.0.12 experimental notices (query precompilation and Native AOT), and no other warning. No suppression or dependency was changed. An initial trimmed/non-AOT invocation used the wrong profile; its errors were retained, then the supported profile was used.

The native binaries passed:

```sh
python3 scripts/verify-flow-v9-published.py \
  --cli /tmp/gnougo-v12-publish/cli/GnOuGo.Flow.Cli \
  --server /tmp/gnougo-v12-publish/server/GnOuGo.Flow.Server \
  --data-directory /tmp/gnougo-v12-published-recovery
```

Results: encrypted receipts, native EF query/index rebuild, tenant/revision isolation, retained results, server restart recovery, streamed durable human answers and no workflow plaintext in the database all passed. Log: `/tmp/gnougo-v12-published-recovery.log`. The new metadata's Native AOT round trip is exercised by the Planning smoke; normal receipt recovery is exercised independently by these published binaries and the new Flow regressions.

These tests use disposable local stores. No campaign ledger, reservation, saved workflow, inference receipt or live session was modified.
