# GnOuGo.Flow.Mermaid

AOT-compatible Mermaid diagram renderer for GnOuGo.Flow YAML workflows.

The package parses a workflow YAML document with `GnOuGo.Flow.Core` and emits Mermaid `flowchart` diagrams. It renders the selected main workflow and can also emit separate diagrams for local sub-workflows referenced by `workflow.call` or static local candidates in `workflow.route`.

## Usage

```csharp
using GnOuGo.Flow.Mermaid;

var result = MermaidWorkflowRenderer.Render(yaml, new MermaidRenderOptions
{
    SubWorkflowMode = MermaidSubWorkflowMode.ReferencedLocalOnly,
    IncludeEmitSteps = false,
    GuardRenderMode = MermaidGuardRenderMode.EdgeLabel
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
- `emit` steps are hidden by default to keep diagrams focused on execution structure. Set `IncludeEmitSteps = true` to show them.
- Step `if` guards are rendered as incoming edge labels by default. Set `GuardRenderMode = MermaidGuardRenderMode.NodeLabel` to render them in node labels.
- The library is pure C# and does not require a Mermaid runtime.
