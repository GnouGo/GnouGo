# Structured-output schema portability

Session `a425b85bb133470cbab4e5f201b9d729` stopped after two planning calls and zero repairs. Its first discovery request succeeded. The second issued schema imported an authoritative workspace restriction containing negative lookahead. The retained schemas reproduce the portability violation without inference; the previous validator accepted both.

The correction projects a clone of each completed new response schema before packing, hashing and persistence. Only valid runtime patterns outside the JavaScript Unicode/nonbacktracking intersection are omitted from the wire schema. Original producer contracts still appear in context and govern semantic validation, MCP boundaries and repair fingerprints. Unsupported remaining shapes fail strict preflight with a schema path, before call/repair reservation. Saved requests, approvals, workflows and storage format 10 are unchanged.

## Reproduction and campaign

The sanitized fixture contains both issued schemas, the successful discovery response and stopped-session diagnostics. Request prompts and host configuration are excluded. Original encrypted evidence is retained.

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter FullyQualifiedName~StructuredOutputProjectionTests
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~StructuredOutputWireTests|FullyQualifiedName~BenchmarkCampaignTests'
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --schema-portability diagnose --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --schema-portability inspect --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002
```

The diagnostic uses a new identity, one physical attempt, the original schema and the existing encrypted EUR 50 campaign gate. It cannot overwrite or repeat its identity. A verified terminal schema rejection is retained as failure, never as a completed model response; uncertain transport outcomes remain conservatively reserved. Model/policy and limits are pinned by the existing adapter (`gpt-5.5-2026-04-24`, medium, 96,000 input, 32,768 output, eight planning attempts, two repairs).

The bounded diagnostic confirmed the provider message: “Invalid JSON schema: regex lookaround is not supported.” It identified the imported workspace pattern in `$defs.d10`, with one physical attempt and no execution.

The first fresh Amazon diagnosis passed provider validation but stopped after four calls and two repairs: the model treated `always` as mandatory normal work rather than cleanup placement, and repeatedly assigned execution placement to data-only outcomes. The three responses are retained in `live-outcome-placement.json`. Version-2 prompting now defines the existing placement semantics explicitly, and new schemas constrain data outcomes to `always=false`, `conditional=false`, `coverage=once`. No accepted outcome or saved request is rewritten.

Live execution results are pending. The historical 33/33 benchmark is unchanged and is not evidence for this correction. Deployment requires rebuilding/restarting the updated host and new planning sessions; the rejected session is not replayed automatically.
