# GnOuGo.Flow.Mermaid

AOT-compatible Mermaid diagram renderer for GnOuGo.Flow YAML workflows.

The package parses a workflow YAML document with `GnOuGo.Flow.Core` and emits Mermaid `flowchart` diagrams. It renders the selected main workflow and can also emit separate diagrams for local sub-workflows referenced by `workflow.call` or static local candidates in `workflow.route`.

## Usage

```csharp
using GnOuGo.Flow.Mermaid;

var result = MermaidWorkflowRenderer.Render(yaml, new MermaidRenderOptions
{
    SubWorkflowMode = MermaidSubWorkflowMode.ReferencedLocalOnly
});

File.WriteAllText(result.Main.SuggestedFileName, result.Main.Content);

foreach (var subWorkflow in result.SubWorkflows)
    File.WriteAllText(subWorkflow.SuggestedFileName, subWorkflow.Content);
```

## Build

```bash
dotnet build src/GnOuGo.Flow.Mermaid/GnOuGo.Flow.Mermaid.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Flow.Mermaid.Tests/GnOuGo.Flow.Mermaid.Tests.csproj
```

## Notes

- `kind: local` workflow references are resolved from the same YAML document.
- `kind: workspace`, `kind: url`, database routes, and generated workflows are shown as call/router nodes but are not fetched or executed by the renderer.
- The library is pure C# and does not require a Mermaid runtime.
