# Windows Amazon first-ten qualification

The opt-in `amazon-observed-first-products-v12` profile and `amazon-business-v16` verifier qualify the ordered prefix needed by the workbook. The source is the complete owned search for the actual query. Original records, including everything after the prefix, remain available in the Browser capture.

An observed product-bearing card occupies one position, including a sponsored card. The verifier groups links by their captured semantic occurrence and ASIN ancestor. Contradictory primary identities, overlapping occurrences or an unresolved earlier card block selection. A known card can supply membership evidence for its auxiliary review and promotion links. The evidence identifies the original records, their classification and the product. An empty-ASIN card requires identifiable product evidence or owned, ordered destination reads. For a necessary listing read the verified chain may end at the exact observed deals page or at the approved seasonal destination after a redirect. Each selected product still needs its own later visit, observed fields and matching workbook row.

## Reproduction on Windows

From the repository root and the .NET SDK configured for this checkout, run the controlled suite with the following command.

```powershell
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release --filter 'FullyQualifiedName~Amazon'
```

Replay original captures without a model, network connection or campaign access. The command reports the raw file hash, the complete outcome under both versions and, when relevant, the prefix preceding a recorded failure.

```powershell
$q = Join-Path $env:LOCALAPPDATA 'GnOuGo-Pr120-Qualification'
$bench = 'tests/GnOuGo.Agent.Planning.Benchmark/bin/Release/net11.0/GnOuGo.Agent.Planning.Benchmark.dll'
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -c Release
dotnet $bench --amazon-first-products-replay "$q/amazon-source-v11-win-04/events.json" 'chaussure geox homme 45'
dotnet $bench --amazon-first-products-replay "$q/amazon-source-v11-win-05/events.json" 'chaussure geox homme 45'
dotnet $bench --amazon-first-products-replay "$q/amazon-source-v12-win-01/events.json" 'chaussure geox homme 45'
```

The previous Windows report supplies the workspace, campaign, effective model, transport and budget argument setup. For a new authorized Amazon run use the new profile and a fresh identity. Review the resulting concrete artifact in the attached terminal. [Windows campaign and budget setup](pr120-windows-20261125.md)

```powershell
$budget = @(Get-WindowsCampaignBudgetArgs)
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
dotnet $bench --schema-portability server-live-plan @common @budget @transport @phases @business --case amazon --run "amazon-first-ten-$stamp" --amazon-first-products --amazon-scope-approval user-amazon-first-products-20261125
```

## Historical replay

* Windows v11 capture 04 contains four successful store destination proofs before the failed legacy listing read. Replaying that recorded prefix under v16 selects ten products, beginning with sponsored ASINs `B0H3KRKGNN` and `B0H3KVGKL2`. There are no later business visits in that prefix.
* Capture 05 records two distinct sponsored review anchors at indices 79 and 85. Their cards have the same respective ASINs as the first two products above. An earlier banner contains four unresolved links. V16 requests its first owned destination, which cannot be supplied by this historical capture.
* Both archived v15 outcomes retain their original failures. The historical listing response records the 302 to `/blackfriday` only. The controlled tests supply a separate synthetic complete chain.

## Fresh Windows results

| Check | Observed result |
| --- | --- |
| GitHub MCP | A fresh GNouGo Server process listed 47 tools on the configured official endpoint, including both review tools. |
| Controlled Amazon suite | 493 passed, zero failed or skipped. The release benchmark built with zero warnings and errors. |
| `amazon-source-v12-win-01` | The complete search contained 1,612 records. Four owned GETs resolved the preceding banner to store destinations. The verifier selected ten products and classified ten reviews and four promotions from their own cards. |
| `amazon-visits-v12-win-02` | A separate live search contained 1,604 records. Four store GETs and ten separate product detail visits passed the independent order, document identity and navigation checks. Browser closed on both diagnostics. An immediate active-page probe confirmed closure each time. |
| `win-amazon-first-ten-01` | The funded Server launcher at `ed06e1e6b3cab435584324f152ab291cdc8b0a8a` stopped before creating a planning session or making a provider call. The selected deployment was `OpenAi / gpt-6.1-terra`. |

Amazon changed the last three positions between the two fresh searches. Each run used its own captured order. The separate Browser diagnostic issued the detail visits directly. Final plan review, Server execution, extracted workbook rows and independent XLSX readback remain outstanding.

The persisted €20 grant still selects its original host configuration. The read-only deployment comparison after the GitHub credential update found an exact match for the registered campaign configuration and its authorization. Only the full host fingerprint differed. The ledger covers the entire effective configuration, including MCP settings. The comparison cannot identify the changed field from that hash alone. A verified credential-rotation amendment needs a separate decision because it would read the earlier encrypted GitHub credential and extend the funded configuration chain.

This session incurred €0. The cumulative Windows tariff estimate was €0.001147691, with €19.998852309 available, zero holds and zero unknown attempts. The predecessor balance remains unknown. [Shareable receipts, ordered ASINs and raw trace hashes](pr120-first-products-20261125.json)

The original Browser events, closure probes, deployment comparison and launch transcript are under `%LOCALAPPDATA%\GnOuGo-Pr120-Qualification`. To reproduce the independent ten-visit check from its original events, run the following command with the variables defined above.

```powershell
dotnet $bench --amazon-first-products-replay "$q/amazon-visits-v12-win-02/events.json" 'chaussure geox homme 45'
```
