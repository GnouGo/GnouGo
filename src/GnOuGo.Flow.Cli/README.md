# GnOuGo.Flow.Cli

The CLI validates and executes Flow workflows, with Planner v2 injected for
`workflow.plan`. Configure a model and MCP capabilities in `appsettings.json` or
configuration overrides. Console prompts collect clarification, mandatory business
behavior acceptance and final approval. YAML is produced by deterministic lowering
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
pass `--run-id <id>` with the same workflow inputs to reopen its planning session after restart.
This resumes planning state; other workflow steps execute according to the workflow itself.
Completed model receipts are reused and an unverifiable dispatch stops without another call.
