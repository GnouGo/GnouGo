# Read-only retained mapping replay

This evidence executable reads the approved v13 live journal through
`EncryptedWorkflowRunStore.ReadAsync` and the original item contract from the
approved YAML. It does not acquire a workflow lease, advance the engine, contact
an MCP/provider, write encrypted storage or update a mapping cache.

From the repository root, with the original encrypted workspace available:

```sh
dotnet build docs/evidence/compact-bindings-v13-live-2026-10-12/offline-replay/RetainedMappingReplay.csproj -warnaserror -p:RepositoryRoot="$PWD"
dotnet docs/evidence/compact-bindings-v13-live-2026-10-12/offline-replay/bin/Debug/net10.0/LiveReadOnlyInspection.dll /path/to/retained-workspace docs/evidence/compact-bindings-v13-live-2026-10-12/workflow-r8.yaml
```

Do not run the workflow or redispatch its saved requests. The helper asserts exact
page/candidate counts and unchanged input hash. It evaluates the original script
and two labelled diagnostic ablations. Neither ablation is a proposed business
mapping; broad link counts do not establish product relevance. Timing/allocation
samples include a cold first evaluation and warmed later evaluations, so they
must not be presented as equivalent-work performance comparisons.

The workspace and observations are private and are intentionally not committed.
The helper's model-usage snapshot is runtime bookkeeping; authoritative EUR
accounting is in the campaign evidence, not its zero `EstimatedCost` field.
