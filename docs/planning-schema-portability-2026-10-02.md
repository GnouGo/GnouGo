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

A second diagnosis passed three provider requests, then correctly rejected a proposal with `outcomeBindings: null`. The version-2 prompt had lost the version-1 requirement to bind every outcome when supplying a plan. That instruction and a wire-property description are restored; the unchanged semantic validator still rejects unsupported plans. The original response is retained in `live-missing-bindings.json`.

A code diagnosis passed four provider requests but exposed missing producer effect metadata on Git, Cmd and Copilot operations. The original responses and diagnostics are retained in `live-code-metadata.json`. These producers now publish explicit version-1 effects through the existing MCP metadata transport, preserving artifact metadata, argument schemas and permission enforcement. Five real-process discovery regressions failed before the correction and pass afterward. Refresh discovery after upgrading these MCP servers; changed contracts invalidate affected approvals through existing fingerprints.

Further diagnostics retained in `live-outcome-mapping.json` and `live-code-effects.json` reject nested task ports reported as root outputs, skipped unconditional work, and effects unsupported by the selected operation. New guidance specifies root output names, initial conditional placement and exact declared effects; validation remains unchanged. The retained 24,000-token discovery test exposed excess prompt text. Compacted guidance restores that test without increasing its limit or removing contracts.

Pattern checking uses Acornima's ECMAScript Unicode validator directly, plus .NET nonbacktracking parsing, with one per-request pattern cache. Creating a scripting engine for this check unnecessarily rooted CLR interop in Native AOT Copilot builds; direct parsing removes that dependency path without warning suppression.

The first frozen cohort (`final`, source `3a2844f0`) contains one stopped Amazon run before HTTP admission and no execution. It remains incomplete and is not pooled with subsequent candidates. A distinct `--cohort final2` retains new manifests and run identities within the same EUR 50 campaign. Read-only `report` includes all six required evaluations, including absent, failed and interrupted runs; mismatched manifests are rejected. Accounting separates planning inference, execution inference and cumulative campaign totals.

The `final2` code run reached review after three planning calls and no repairs. Actual Git/Copilot/Cmd execution cloned the pinned head, requested local command permissions, attempted installation/lint/tests, failed with a proxy 502, and cleaned up the checkout. No report or complete command receipts were produced; the execution oracle failed. [Sanitized evidence](planning-schema-portability-final2-code-failure.json) is retained. The proxy did not retain its exact rejection reason, so this run alone does not prove which admission check stopped it.

Two deterministic harness defects were corrected before a separate `final3` cohort: execution inherited the eight-attempt **planning** limit, and the execution input guard compared JSON bytes with a token limit. Execution now shares only the EUR ceiling, uses pinned `o200k_base` token estimation with framing headroom, and retains the larger byte-based monetary reservation. [OpenAI's model mapping](https://github.com/openai/tiktoken/blob/main/tiktoken/model.py) specifies this encoding for GPT-5. Proxy failures now retain encrypted admission evidence. The failed cohort remains separate; no saved workflow or failed execution is rerun.

Live execution results are pending. The historical 33/33 benchmark is unchanged and is not evidence for this correction. Deployment requires rebuilding/restarting the updated host and new planning sessions; the rejected session is not replayed automatically.
