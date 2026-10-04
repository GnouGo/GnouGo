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

The `final3` Amazon execution reached Amazon.fr and completed its first provider inference, but Flow then raised `LLM_NETWORK`: telemetry called `GetValue<int>()` on a valid `JsonValue<long>` token count. An independent real-Flow regression reproduced the same failure with Int64-backed and large parsed JSON integers (two failures before correction). Telemetry now reuses the accounting reader; all 44 affected Flow tests pass. No permission or execution contract changes are involved.

That run also reported `closed=false` because the configured browser had `KeepBrowserOpen=true`. A subsequent disposable evaluation process explicitly disables that debug option; persisted host configuration is unchanged. The frozen manifest records this setting. Earlier cleanup failures remain failures. Read-only reports now reject missing manifests and distinguish interrupted runs from unstarted slots.

The `final3` code execution retained two transport failures: concurrent permission callbacks violated MCP's single outstanding request rule, and the client eventually exceeded its ten input-required rounds. Eight concurrent callbacks reproduce the first error without inference; a per-invocation gate now serializes them and preserves individual refusals/answers. All 200 Copilot MCP tests pass. The [SDK 2.2.0 client](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.Core/Client/McpClientImpl.cs) hardcodes the ten-round limit; that separate dependency limitation remains open. No broad grant, permission bypass, protocol downgrade or automatic replay was introduced. [Sanitized execution failures](planning-schema-portability-final3-failures.json) retain both the Amazon and code observations.

## Final live result: acceptance not met

Frozen source and harness: `3e8869676b6099dff6f445143f8368c16eaf709e`, cohort `final4`. The [manifest and six measurements](evidence/schema-portability/final4.json) record corpus/harness hashes, configured-policy fingerprint, model, limits, OS and framework. The [statistical summary](evidence/schema-portability/final4-summary.json) uses nearest-rank p95. Earlier cohorts and diagnostics remain separately identified in encrypted storage; none is pooled into this result.

**0/6 execution oracles passed**: Amazon 0/3, code 0/3. Five plans stopped before execution. The one valid code artifact was explicitly reviewed and executed against the pinned disposable checkout. Version checks and repository inspection reached permission callbacks, but the MCP client stopped at its ten-round input-required limit. The concurrent-callback error did not recur. Cleanup independently succeeded; no review.json or complete command receipts were produced. Installation, lint/tests and complete review are **not established**. [Sanitized final execution evidence](evidence/schema-portability/final4-code-failure.json) excludes repository feedback and private payloads.

| Evaluation | Planning calls / attempts | Discovery reads | Repairs | Planning input / output tokens | Planning seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Amazon 1 | 4 / 4 | 2 | 2 | 26,208 / 5,621 | 78.3 |
| Amazon 2 | 4 / 4 | 2 | 2 | 26,497 / 8,731 | 185.2 |
| Amazon 3 | 5 / 5 | 2 | 2 | 37,450 / 9,836 | 115.2 |
| Code 1 | 3 / 3 | 3 | 0 | 40,345 / 8,957 | 115.1 |
| Code 2 | 5 / 5 | 3 | 2 | 52,633 / 8,386 | 115.4 |
| Code 3 | 5 / 5 | 3 | 2 | 48,272 / 10,346 | 155.6 |

Code 1 execution added **3 logical calls / 3 physical attempts**, **27,139 input / 1,207 output tokens**, **405.4 seconds** execution and **520.5 seconds** total latency. Other final runs had no execution inference. Final-cohort cost: **EUR 2.5538** (planning 2.4016, execution 0.1522); final verified tokens: **258,544 input / 53,084 output**.

Across all six evaluations, planning median/p95: **4.5/5 calls**, **2.5/3 tools/list reads**, **2/2 repairs**, **38,897.5/52,633 input tokens**, **8,844/10,346 output tokens**, and **115.3/185.2 seconds**. Total median/p95 latency is **135.5/520.5 seconds**. Failed planning ends the measured run with zero execution time; these zeros do not demonstrate efficient execution. Human waiting is included. Missing or interrupted slots remain failures; unknown usage is never imputed as successful work.

The entire new campaign, including the original diagnostic and every failed iteration, recorded **102 physical attempts**, **1,111,677 verified input / 173,498 output tokens**, and **EUR 9.5268** estimated cost, with **zero uncertain cost reservations**. Costs use provider usage, model price metadata and FX quotes, not invoices. The terminal HTTP 400 diagnostic has no token-usage receipt; its verified rejection is retained separately. The EUR 50 ceiling was not reached. Paid collection has stopped after the failed final evaluation.

Before correction, the retained session stopped on its second planning call with zero repairs; the separately authorized exact-request diagnostic confirmed the provider rejection in **4.456 seconds**. After correction, **78/78 admitted planning requests** across the new campaign completed without a provider-schema rejection, including discovery expansions and repairs. This establishes schema acceptance, not workflow correctness. There is no comparable successful baseline execution, so no execution speedup or universal reduction in calls/tokens is claimed.

Remaining issues:

- Amazon 1–2: generated outcome mappings, required-path coverage and normal/cleanup placement remain invalid. Amazon 3: missing scope exports/references and an unauthorized repair change. All stopped before external execution; the final cohort produced no XLSX for comparison.
- Code 2–3: accepted cleanup effects (`write` or `lifecycle`) conflict with the selected command contract's `execute` effect. Scoped repair preserves accepted requirements rather than relabelling them to pass.
- Code execution: the SDK's fixed input-required-round limit blocks longer interactive calls. Required repository toolchains, successful dependency installation and complete check evidence remain unverified. No broad approval, sandbox expansion, protocol downgrade or automatic reconciliation was used.
- Real Amazon navigation was observed only in the separately retained `final3` execution before its telemetry failure; no full product extraction was validated. CAPTCHA or absent data cannot be inferred from an unexecuted search.

## Reproduction, validation and rollout

[Collection/review/execution/report commands](../tests/GnOuGo.Agent.Planning.Benchmark/README.md#authorized-schema-portability-live-campaign) use the encrypted journal. Reproduce the frozen build from the recorded SHA in an isolated checkout. Further collection requires a fresh cohort identity within the existing authorization and remaining campaign ceiling; a separate campaign needs separate authorization. Never overwrite or replay these runs. Reports reject mismatched or missing manifests.

Recompute the published summaries from the sanitized report (zero execution duration means execution never started):

```sh
python3 - docs/evidence/schema-portability/final4.json <<'PYTHON'
import json, math, statistics, sys
rows = json.load(open(sys.argv[1]))["runs"]
for name, read in {
    "calls": lambda r: r["calls"],
    "discovery_reads": lambda r: r["discovery_reads"],
    "repairs": lambda r: r["repairs"],
    "input_tokens": lambda r: r["accounting"]["planning"]["known_input_tokens"],
    "output_tokens": lambda r: r["accounting"]["planning"]["known_output_tokens"],
    "planning_ms": lambda r: r["planning_ms"],
    "total_ms": lambda r: r.get("total_ms", r["planning_ms"]),
}.items():
    values = sorted(map(read, rows))
    print(name, "median", statistics.median(values), "p95", values[math.ceil(.95 * len(values)) - 1])
PYTHON
```

Validation on the frozen implementation:

- `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`: **4,040 passed, 0 failed, 13 opt-in/platform skips**, 33 projects. Focused schema/projection, wire-payload, recovery, permission, usage and benchmark regressions pass. The unchanged 24,000-token discovery regression passes.
- All 200 Copilot MCP tests pass; four permission-management tests also pass against the published Native AOT executable. Published Git/Cmd contract checks pass. Flow packages pack independently without warnings.
- Native AOT planning smoke passes, including authoritative path rejection after projection. The trimmed published Agent.Server encrypted recovery smoke passes; planning format 10 / execution journal 9 remain unchanged. Frontend production build and planning-skill validation pass.
- The [Linux planner pipeline](https://github.com/GnouGo/GnouGo/actions/runs/37005432309) passes on `3e886967`; [stable .NET/Python, Agent.Server and Linux server-publish jobs](https://github.com/GnouGo/GnouGo/actions/runs/37005432743) also pass. Other platform/package jobs may still be running; they do not change the failed live gate.

The updated source host was rebuilt/restarted and verified at `http://127.0.0.1:5168`. All 43 saved Designer sessions were inactive before restart; none was replayed or modified. The process's embedded OTLP listener is disabled because the existing local collector already owns its ports; saved configuration is unchanged. Refresh MCP discovery and create new planning sessions. Keep the rejected session as evidence. For automated browser cleanup, the target host's existing `KeepBrowserOpen` debug option must be disabled; this campaign changed only its disposable browser process.

PR #117 remains **draft** because the six execution oracles failed. No SmartGuide feedback or proposed diff comments were published; publication remains untested. The historical **33/33** benchmark is unchanged and is not evidence for these later corrections.
