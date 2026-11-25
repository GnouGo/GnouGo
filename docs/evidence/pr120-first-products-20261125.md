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
