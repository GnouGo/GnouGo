# Planning evaluation corpus

Three independent expectations: local arithmetic returns 42; a declared read result of 21 transforms to 42; a protected workflow invokes write then cleanup once and returns 42. All external integrations are mocked. Offline fixtures assert one interpretation call, zero repairs, isolated scenarios and fresh approval.

```bash
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark -- --live-command /absolute/path/to/model-adapter
```

A live adapter reads one source-generated `LLMRequest` JSON from stdin and returns one `LLMResponse` JSON on stdout. It must honor the supplied strict schema, reasoning, request identity and token ceilings. Configure credentials in the adapter's own environment. Each invocation is bounded to two minutes. The corpus never connects to external MCP servers. Output reports model calls, repair attempts and scenario counts; any wrong result or effect sequence exits unsuccessfully. No live-model score is implied by an offline fixture run.
