# Windows credential rotation and Amazon admission

The funded Windows configuration is `db68ae1900ecd8e640bdfe4e5c2909ed999459868d50f145771fac291bec6ca8` in campaign `pr120-windows-20261125-20`. The PR started at `0a7c950f`. All work used the separate Windows checkout, preserving the local generated model catalog edit.

## Protected live comparison

The trusted KeyVault service read versions 1 and 2 of the GitHub HTTP MCP configuration. Version 2 matched the active `Github.ApiKey`. The credential values differed. An in-memory copy of the active host reproduced the current fingerprint before substitution. Replacing only that field with its version 1 value failed to reproduce the funded host fingerprint. The campaign configuration and declared model metadata matched. The historical credential was never used for an outbound operation.

KeyVault version metadata identified a second write. At 19:00:06 UTC the GitHub HTTP MCP moved to version 2. At 19:00:50 UTC the bundled Git MCP token override also moved to version 2. The latter is a separate KeyVault record mapped to `GnOuGo.Git.Mcp / Git__Token`. Its version history establishes a write. The one-field proof did not read its historical credential or attribute the remaining difference to it.

| Live check | Outcome |
| --- | --- |
| Registration exists, material hash, campaign configuration, authorization binding | Pass |
| Full host fingerprint, including after the one-field reconstruction | Fail |
| Model and declared metadata | `OpenAi / gpt-6.1-terra`, metadata hash matched |
| New configuration, provider calls, planning session, final review and execution | None |
| Excel and cleanup | No Server workflow started. No new workbook or Browser process was produced by this admission attempt. |
| Windows accounting | €0 new spending, €0 holds, €0.001147691 cumulative tariff estimate, €19.998852309 available. The predecessor balance remains unknown. |

## Controlled validation

The verifier binds the immutable amendment to its parent, both host fingerprints, declared metadata, MCP server, field, KeyVault record identity and the selected versions. The campaign checks the funded chain after restart. Isolated tests rejected an incorrect historical or active value, record/version substitution, changed endpoint, permissions, model metadata, a second MCP credential change and extra proof fields. With an isolated encrypted fixture, separate OS processes verified preview, idempotent apply and cold admission against the original test grant.

The tested verifier is `37b983be926375076015fce2f12d0214c0133e25`. All 24 KeyVault core tests and 253 campaign tests passed. The final proof-shape change passed all nine focused rotation cases. The Release benchmark build reported zero warnings and errors.

The solution-wide run also found Windows cleanup failures outside the rotation tests. In Flow.Integrations, 27 tests failed and 97 passed. The first failure was an open `vault.db` when `CapabilitySnapshotJournalTests` deleted its temporary directory. The private solution log is `rotation-solution.log`. [Machine-readable admission checks, accounting and receipt hashes](pr120-credential-rotation-20261125.json)

The private receipts are in `%LOCALAPPDATA%\GnOuGo-Pr120-Qualification`, including `rotation-preview-error.log`, `rotation-version-dates.txt`, `rotation-deployment.json`, `rotation-audit.json` and `rotation-cold-reopen.log`. The previous ordered-visit diagnostics remain available in the [Amazon evidence](pr120-first-products-20261125.md).

## Reproduction

Use the Windows checkout, workspace and `$common` argument array from the [Windows campaign setup](pr120-windows-20261125.md). Use the existing configuration ID above in those common arguments. The following operations inspect the live ledger and exercise the controlled tests.

```powershell
dotnet test tests/GnOuGo.KeyVault.Core.Tests -c Release -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -warnaserror --filter 'FullyQualifiedName~BenchmarkCampaignTests'
dotnet $bench --schema-portability financial-audit @common
dotnet $bench --schema-portability inspect-deployment @common
```

The original preview was run with the following additional arguments. It reads the selected historical version and returns an error if the exact reconstruction fails.

```powershell
$registration = @(
  '--capture-current-settings', '--github-credential-amendment',
  '--authorization', 'user-pr120-verified-github-rotation-20261125',
  '--previous-configuration-id', 'db68ae1900ecd8e640bdfe4e5c2909ed999459868d50f145771fac291bec6ca8',
  '--expected-configuration', '651383e78ea7112a8ad4dd8d7767d8d6114f3da0f4e34186a7e1a625236f1827',
  '--previous-credential-version', '1', '--current-credential-version', '2'
)
dotnet $bench --schema-portability register-configuration @common @registration
```
