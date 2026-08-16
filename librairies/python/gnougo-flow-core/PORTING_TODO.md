# Python parity ledger — `gnougo-flow-core`

## Reference baseline

- Behavioral source of truth: `src/GnOuGo.Flow.Core/` at commit `c4b069a`.
- Reference verification: 919 passing tests in `tests/GnOuGo.Flow.Tests/` and 33 passing tests in `tests/GnOuGo.Flow.Integrations.Tests/`.
- Python verification: 360 passing tests in `librairies/python/gnougo-flow-core/tests/`.
- Target: this independent Python 3.10+ package. It does not load or execute .NET binaries.
- Conflict rule: preserve compatible Python extensions, but follow the .NET behavior when contracts conflict.

The parity work represented by this ledger is complete for the baseline above. A later .NET change requires a new comparison and a new ledger entry; the word "parity" here is intentionally tied to the recorded commit.

## Implemented feature matrix

| Area | Implemented Python behavior | Focused coverage |
|---|---|---|
| Contracts | `nullable`, `type: [T, null]`, typed defaults, explicit empty `required_properties`, nested input/output definitions | `test_contract_parity.py`, `test_json_schema.py` |
| Compilation | `steps` plus `finally`, global step-ID uniqueness, finalizer validation, local-call cycle analysis | `test_contract_parity.py`, `test_parser_compiler.py` |
| Lifecycle | Finalizers after success, failure, cancellation, resume, `workflow.call`, and `workflow.execute`; independent timeout/budget; `data.workflow_error`; primary-error precedence; outputs after cleanup | `test_finalization_parity.py` |
| Type analysis | Recursive closed-object and required-field validation, nullable optional use sites, guaranteed `set` fields, guard/comparison inference | `test_contract_parity.py`, `test_workflow_plan_parity.py` |
| JSON Schema | Nullable normalization, `if`/`then`/`else`, `dependentRequired`, enum/const diagnostics, final MCP request validation | `test_contract_parity.py`, `test_mcp_context_elicitation_parity.py` |
| WFScript | Balanced nested JSDoc types, safe parameter completion, distinct timeout and statement-limit failures | `test_contract_parity.py`, `test_scripting.py` |
| Human input | Boolean confirm normalization including custom labels, waiting/resumed telemetry | `test_human_input.py`, `test_phase5_mcp_human.py` |
| Routing | Missing/invalid-only HITL forms, retry/coercion, sequential form collection before parallel execution, non-string answer serialization, nested failure details | `test_workflow_route_executor.py` |
| LLM failures | Timeout and HTTP provider failures use stable retryable `LLM_TIMEOUT` / `LLM_NETWORK` and non-retryable `LLM_PROVIDER` contracts without converting caller cancellation | `test_llm_failure_classifier.py` |
| LLM integration | Routing adapters preserve background mode, maximum output tokens, tools, structured output, and response fields | `test_routing_llm_adapter.py` |
| MCP discovery | Tool `_meta`/`meta`, `output_schema`, and examples survive adapters and cache; one live `tools/list`; concurrency-safe session reuse | `test_mcp_context_elicitation_parity.py`, `test_mcp_factory.py` |
| MCP calls | Optional-null omission, conditional-schema validation, host-owned correlation metadata, domain-neutral context isolation, recursive reserved/secret-key rejection | `test_mcp_context_elicitation_parity.py` |
| MCP elicitation | Adapter-to-HITL bridge, exact-call correlation, safe sole-active fallback, waiting/resumed/refused/cancelled phases, concurrent-call isolation | `test_mcp_context_elicitation_parity.py` |
| MCP cancellation | Caller cancellation, configured timeout, and transport cancellation remain distinct | `test_mcp_context_elicitation_parity.py`, `test_mcp_progress_timeout_parity.py` |
| Capability preflight | `off`, `explicit`, and `infer`; exact alternatives, RFC 6901 bindings, selector-aware denials, optional/repeated operations, fail-closed discovery, bounded catalogs | `test_capability_preflight_parity.py` |
| Planning safety | Mandatory confirmation before inferred external writes unless unattended, multiset capability locks, exact call/binding validation, artifact provenance, redundant producer errors | `test_capability_preflight_parity.py` |
| Planning repair | Structured diagnostic fingerprints, two-unchanged-attempt stall detection, scoped surgical repair of a target and its direct consumers | `test_capability_preflight_parity.py`, `test_workflow_plan_repair_scope_parity.py` |
| Pipeline planning | Background-capable planning, strict provider schemas, `work_kind`, `contract_role`, `concrete_outcome`, catalog IDs, locked-operation ownership, planned tools, finalizers, and typed contracts retained through extraction, leaf generation, assembly, validation, and reporting | `test_workflow_plan_parity.py`, `test_workflow_plan_pipeline_quality_analyzer.py` |
| Telemetry | Planning usage/cost attributes, nested lifecycle events, MCP correlation, human-input phases, finalization errors | planner, runtime, MCP, and route test modules |

## Stable error additions

- `LLM_PROVIDER`
- `CAPABILITY_PREFLIGHT_UNAVAILABLE`
- `CAPABILITY_PREFLIGHT_DISCOVERY_FAILED`
- `CAPABILITY_PREFLIGHT_INFERENCE_FAILED`
- `CAPABILITY_PREFLIGHT_REDUNDANT_ARTIFACT_PRODUCER`
- `WORKFLOW_PLAN_REPAIR_STALLED`
- `WORKFLOW_FINALIZATION_FAILED`
- `WORKFLOW_FINALIZATION_TIMEOUT`

The existing structured plan, schema, MCP, HITL, cancellation, and runtime errors remain supported.

## Compatible Python extensions

- `loop.sequential.input.over` remains available as a Python extension alongside the shared `times` and `while` modes.
- MCP transports remain injected adapters. The core package deliberately has no mandatory MCP SDK and does not own HTTP or stdio processes.
- Unlike the .NET package split, the Python integration adapters remain in the single `gnougo-flow-core` distribution to preserve its existing public imports; this is a packaging-only divergence, not a workflow behavior divergence.
- Python protocols and in-memory adapters remain public test seams.
- Snake-case Python model fields remain canonical in Python; accepted .NET/MCP aliases are normalized at boundaries.

These extensions must not weaken shared workflow validation or change behavior for DSL that is valid on both runtimes.

## Validation commands

From `librairies/python/gnougo-flow-core/`:

```bash
uv run --extra dev python -m pytest -q
uv run --extra dev ruff check .
```

From the repository root, verify the unchanged source-of-truth suite:

```bash
dotnet test tests/GnOuGo.Flow.Tests/GnOuGo.Flow.Tests.csproj
```

Release versions are injected by the existing release pipeline. Do not set a package version manually as part of parity work.

## Updating this ledger

When `GnOuGo.Flow.Core` changes:

1. Record the new source commit and .NET test count.
2. Compare contracts, execution behavior, errors, prompts, telemetry, and limits—not C# file layout.
3. Port affected test intent into independent Python tests.
4. Document any compatible Python extension and every deliberate divergence.
5. Run all three validation commands above.
