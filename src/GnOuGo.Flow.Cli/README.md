# GnOuGo.Flow.Cli

The CLI validates and executes Flow workflows, with the typed planner injected for
`workflow.plan`. Configure a model and MCP capabilities in `appsettings.json` or
configuration overrides. Console prompts collect typed clarification and final artifact approval. YAML is produced by deterministic lowering
of the validated typed graph. See [planning architecture](../../docs/workflow-planning-v2.md).

```sh
dotnet build src/GnOuGo.Flow.Cli
dotnet run --project src/GnOuGo.Flow.Cli -- validate src/GnOuGo.Flow.Cli/examples/workflow-planning.yaml
dotnet run --project src/GnOuGo.Flow.Cli -- run src/GnOuGo.Flow.Cli/examples/workflow-planning.yaml -i "task=Return a greeting"
dotnet publish src/GnOuGo.Flow.Cli -c Release -r osx-arm64
```

Runtime expression and WFScript support uses the existing Jint sandbox. Published
binaries retain the repository's documented, dependency-specific AOT exceptions.

Planning sessions persist through the encrypted KeyVault record API. The CLI prints a run ID;
use that identity with the revision-checked resume command after restart.
Runs now use the schema-9 encrypted execution journal. Inspect the current revision before resuming the same workflow and run identity:

```bash
gnougo-flow runs --tenant default --id RUN_ID
gnougo-flow run workflow.yaml --run-id RUN_ID --resume-revision REVISION
gnougo-flow runs --tenant default --id RUN_ID --command cancel --revision REVISION
```

Completed receipts are reused. Unknown external outcomes require reconciliation through the run API before execution or cleanup can continue. The CLI reports recovery status and the remaining step ceiling. Reusing a run ID without `--resume-revision` is rejected.
Completed model receipts are reused and an unverifiable dispatch stops without another call.
