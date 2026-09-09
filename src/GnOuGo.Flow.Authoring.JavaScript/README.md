# GnOuGo.Flow.Authoring.JavaScript

Separately publishable .NET 10 JavaScript authoring adapter for Flow.Core's
`IPlanningSourceCompiler`. Uses the repository's Jint 4.16.0 and its Acornima parser.
No Node.js or TypeScript installation is required.

```sh
dotnet build src/GnOuGo.Flow.Authoring.JavaScript/GnOuGo.Flow.Authoring.JavaScript.csproj
dotnet test tests/GnOuGo.Flow.Authoring.JavaScript.Tests/GnOuGo.Flow.Authoring.JavaScript.Tests.csproj
dotnet pack src/GnOuGo.Flow.Authoring.JavaScript/GnOuGo.Flow.Authoring.JavaScript.csproj -c Release
```

The host passes an approved workflow template and declared provider-neutral schemas.
`Describe(context)` supplies SDK/JSDoc guidance. `Compile(source, context, token)`
returns an untrusted `PlanningWorkflow`, diagnostics and node source locations.
The planner must validate the graph, ownership, behavior and producer contracts before
compiling it to native YAML or allowing execution. JSDoc is documentation, not a
static JavaScript type checker.

```js
flow.workflow({
  steps: [flow.step("greeting", "set", {message: "ready"}, {
    outputSchema: {type: "object", properties: [
      {name: "message", schema: {type: "string"}, required: true}
    ]}
  })],
  outputs: [flow.result("message", {type: "string"}, flow.ref("greeting", "message"))]
});
```

The workflow key and matching nodes' capability/operation ownership come from the
template. `flow.input`, `ref`, `structured`, `item`, `previous`, `expr` and `compute`
produce symbolic values. Ordinary JavaScript values in step inputs become literals.
`port`, `result`, `call`, `choose`, `loop`, `parallel` and `when` construct native
graph contracts. Use `finally` and `onError` for native cleanup and failure handling.
Runtime conditions must use these graph operations, not JavaScript truthiness on
symbolic references. The planner still rejects changes to approved control flow.

Each evaluation has a five-second deadline, a 10,000-statement engine limit, 50 MB
memory limit and recursion limit of 64. Source and graph payloads are limited to
256 KiB and 1 MiB respectively. CLR access, host callbacks, network/filesystem access,
imports, clocks and dynamic compilation are not exposed. Runtime helpers are carried
as source in native `functions` and require independently validated parameter/JSDoc
contracts; they cannot capture authoring locals.

This is a bounded graph-construction interpreter, not an operating-system isolation
boundary. The host must never inject service objects or external effects into it.
The existing planning Native AOT smoke publishes and executes an authored workflow.
