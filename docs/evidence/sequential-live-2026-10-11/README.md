# Authorized fresh planning — stopped before review

Frozen candidate: `1cc0fe8aba01cc0eb93e35d4e3de4ff2242581a2`  
Campaign: `schema-portability-20261002`  
Cohort/run: `sequential20261011a` / `sequential20261011a-amazon-1`  
User authorization: one fresh paid planning session; execution requires separate artifact approval.

The session stopped at revision **2** with no compiled artifact or execution approval. **No Browser navigation, product visit, mapping execution or workbook writing occurred.** Discovery made two `tools/list` reads. Both model calls have committed receipts; no uncertain call was replayed and no automatic repair was spent on this compiler failure.

## Exact compiler failure and offline reproduction

`TASK_COMPILER_VALIDATION` at `/tasks/write_products_file/inputs/rows`:

```text
MCP_REQUEST_EXPR_TYPE_MISMATCH: mcp.call request field 'input.request.rows'
resolves to array, but the MCP input_schema requires null.
```

The declared rows are non-null arrays of four nullable strings, with one to eleven rows. The discovered Document contract allows either legacy `content` or structured `rows`, exclusively. The request supplies rows and omits content. The original schema accepts a resolved rows fixture and rejects a fixture supplying both.

Read-only replay through public encrypted-record APIs reproduces the same error after compiling the saved TaskPlan. A reduced fixture with arbitrary `entries`/`payload` names also reproduces it, with **zero provider/MCP calls**:

| Input | Static expression result |
|---|---|
| Typed producer rows reference, original alternatives | Incorrectly requires null |
| Literal nested row array | No expression mismatch |
| Same typed reference, alternatives reversed in disposable clone | No expression mismatch |
| Equivalent generic field names | Same incorrect null requirement |
| Resolved rows against original complete schema | Valid |
| Both alternatives supplied against original schema | Rejected |

`FlowTypeDescriptorConverter.FromJsonSchema` returns a union of alternative descriptors; `StepExpressionTypeValidator.SelectObjectCompatibleType` selects the first object member without considering the request. For the published schema that member is the legacy-content branch, where rows are null. The converter also returns before preserving sibling properties. Reordering the producer schema would only conceal the defect and is **not** an accepted correction.

The deterministic gate covered literal/assembled rows but missed a complete typed-array reference under this object union. This is a validation coverage gap despite the previously green solution/CI. The minimal next correction is conservative expression validation against applicable object alternatives **and** sibling constraints, preserving complete runtime `oneOf` validation, requiredness, exact-one semantics and unknown-value handling. It belongs in existing schema/type validation, not a tool-name exception or another model repair.

See [offline findings](offline-rows-reproduction.json) and [read-only reproduction source](replay-rows.cs). The original encrypted record was compared before/after and is unchanged. No production source was changed during this planning/replay session.

## Separate proposal review failures

The [actual proposal](planning-r2-plan.json) contains 14 tasks, including three extract and three interpretation transforms. The [accepted requirements](planning-r2-requirements.json) remain unchanged.

- The model introduced `accept_cookies` and its conditional branch although the business request contains no consent instruction. This is a generated workflow action, not automatic Browser behavior. Do not approve the added work.
- All three extraction declarations bind whole `observationSnapshot` objects and omit `each`; extraction also selects the first products. They do not use bounded complete pages followed by a compact global ID decision.
- Product URLs are forwarded from learned extraction without the offered-ID then original-record lookup composition.
- `assemble_workbook_rows` uses interpretation for already typed cell copying, and `compute_pre_cleanup_success` uses interpretation for typed status logic.
- Description extraction has no explicit observed block-ID selection and deterministic text reconnection. Compilation alone would not establish verbatim cells.

Both original issued requests contain the updated `each`, verbatim block-ID and no-unrequested-work guidance. This is not a stale binary/prompt. Guidance improved deterministic examples but did not ensure this model response follows them. These findings require explicit composition revision and review; changing the rows validator alone does not make this proposal executable or acceptable. No direct YAML edit, consent prohibition in the compiler or weakened oracle is proposed.

## Accounting and execution status

- Planning: **2 logical calls / 2 physical attempts**, zero repairs/revisions, 19,854 verified input tokens, 8,702 output tokens, **107,136 ms** active planning.
- Added verified cost: **€0.3197533055284408554441387879**.
- Campaign upper bound: **€132.71304909248008370640421330 / €150**; remaining **€17.28695090751991629359578670**.
- All five historical unknown transport reservations remain **€6.5016147947905255187922165013**. No new unknown usage/reservation.
- Execution and all business oracles: **not started / incomplete**. There is no artifact to approve.

Full original requests, responses, receipts and session history remain encrypted. Sanitized [result](planning-result.json) and [frozen cohort](cohort.json) are retained here. The cohort-level historical oracle label remains `real-workflows-v1`; the actual fresh Amazon run explicitly stores `observed-products-v2`. Neither manifest is rewritten.

## Reproducible commands

Use the clean frozen candidate and its already-built harness; do not rerun `plan` for this identity.

```sh
# Historical invocation, already completed — do not repeat:
# --schema-portability plan --campaign schema-portability-20261002 --cohort sequential20261011a --case amazon --run sequential20261011a-amazon-1 --max-products 10

# Read-only inspection/report:
tests/GnOuGo.Agent.Planning.Benchmark/bin/Release/net10.0/GnOuGo.Agent.Planning.Benchmark --schema-portability report --workspace "$GN_WORKSPACE" --campaign schema-portability-20261002 --cohort sequential20261011a
```

The replay source loads the retained record via public KeyVault APIs, compiles only in memory and calls existing validators. Run it in a disposable console project referencing the frozen benchmark dependencies (including their native SQLite dependency), passing the workspace and a disposable evidence directory. It does not start WorkflowEngine, inference or MCP transport.

The validated production candidate retains its green 5,197-test solution and 29 non-skipped CI checks; this new live result exposes missing coverage and does **not** establish acceptance. PR #117 stays draft. Further paid planning/execution is stopped pending correction and the applicable authorization/review gates.
