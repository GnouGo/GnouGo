# Typed TaskPlan transformation restoration

This deterministic regression pass restores semantic transformations within the existing TaskPlan architecture. It adds no planning phase, executable IR, provider-name heuristics or agent workaround. Planning storage remains format 10; execution journals remain schema 9. Issue [#112](https://github.com/GnouGo/GnouGo/issues/112) and draft PR [#113](https://github.com/GnouGo/GnouGo/pull/113) track delivery and final CI.

## Reproduced failure

The retained product-search plan used `value` tasks whose objectives described HTML extraction and TSV formatting, but whose bindings only copied source strings and arrays. A string reached `foreach.items`, and an array reached Document's string content input. The sanitized regression preserves both `TASK_ITEMS_INVALID` and `TASK_INPUT_TYPE`. Raising a budget or rewriting only those consumer bindings cannot restore the missing computation. Existing sessions and encrypted evidence were left untouched.

## Contract changes

- `transform` uses the existing objective and named input bindings, plus `resultType: TaskType`. The result is a nonempty closed object with required, recursively typed fields; nullable fields explicitly represent missing data. Opaque types and defaults are rejected.
- Preflight validates inputs, result declarations and host policy. Deterministic lowering emits fixed `template.render` assembly and strict structured `llm.call`; instructions and supplied data are template values, never template code. Model/provider configuration and inference budgets remain runtime responsibilities.
- Validated structured fields become business ports. Mode-aware template output contracts are shared by graph validation and runtime type resolution; unresolved modes remain conservative. Template execution itself is unchanged.
- Repairs may change diagnosed transform inputs and exact result-type slots. Existing field names, unrelated tasks and choices remain fixed. Existing `value` tasks are never silently promoted; use explicit semantic revision or regeneration.
- Review displays expose transformation inputs and typed results. Serialization and approval recompilation cover the new semantic fields. Absent result types remain omitted, preserving serialization of existing non-transform plans.
- `discoveryRequests` replaces singular proposal `sourceId`/`cursor`: one to four issued page requests, exclusive with a TaskPlan. Validate the whole batch before sequential metadata reads. Recovery reuses the recorded model response and identity; metadata rereads do not create another inference. Superseded pending response schemas stop with `PLANNING_REQUEST_INCOMPATIBLE` and regeneration instructions, retaining their reservations and accounting.
- Document capability descriptions now state the existing creation of parent directories inside allowed roots. No directory-command task or writer behavior change is needed.

## Parent comparison and independent oracle

An isolated checkout pinned `main` at `bf90eb6d5048bd80ba83cb70df69b841007d4d59` ran its existing grounded transformation path. Legacy DTO/envelope adapters stayed in the temporary comparison harness; none were added to production or the current test dependency graph.

Both revisions used the same sanitized HTML fixture and independent expected workbook rows. Planning and runtime inference were scripted; the host tests and parent harness used the real Document writer and inspected actual XLSX cells.

| Data variant | Pinned main | TaskPlan candidate | Runtime model mocks | Workbook rows |
|---|---|---|---:|---:|
| Unicode, commas, quotes, embedded whitespace | Pass | Pass | 4 | 3 |
| Changed names, descriptions and prices | Pass | Pass | 4 | 3 |
| Missing description and price | Pass | Pass | 3 | 2 |
| Empty search | Pass | Pass | 2 | 1 |

The compiled path is Browser → typed URL extraction → bounded sequential product visits and typed extraction → ordered collection → TSV transformation → Document XLSX, with browser cleanup. Embedded tabs/newlines are explicitly normalized before TSV writing. Tests also cover renamed capabilities, reordered tools, invalid structured responses, cancellation, permission refusal, output-path refusal, cleanup, policy denial, type-schema alignment, scoped-repair rejection, approval invalidation and recovery. A well-typed fabricated row deliberately fails the independent oracle: structured validation is not evidence of factual correctness.

## Discovery measurements

The fixture exposes nine Browser and four Document operations among ten unrelated sources. It requires exactly **two scripted planning requests**: one two-source batch and one TaskPlan. Three selected contracts resolve deterministically, with no model-assisted resolution call. Discovery receipts survive recovery and approval; invalid batches fetch no partial pages.

| Request | Prompt characters | Response-schema characters | Conservative input estimate |
|---|---:|---:|---:|
| Discovery batch | 3,339 | 15,586 | 6,565 tokens |
| TaskPlan | 5,699 | 15,202 | 7,223 tokens |

Both fit the unchanged **24,000-token Designer allowance**. Descriptions, exact contracts, the conservative estimator and all existing ceilings remain intact. These are framework-overhead measurements on sanitized metadata, not measurements of the retained live catalog or a live-model reliability claim.

## Deterministic validation

- Full .NET solution: **3,014 passed**, zero failures; five opt-in real Copilot tests intentionally disabled. This includes 264 planner tests, 415 Agent.Server tests and all eight existing business scenarios. No new build warnings.
- Real XLSX integration: four data variants and a refused path passed with cleanup and cell/location assertions.
- Published macOS ARM64 Native AOT smoke: all eight scenarios, typed transformations and source-generated batch/TaskPlan serialization passed, with existing documented publish-local dependency exceptions only.
- Skill frontmatter and current local documentation links validated. Package, Python and branch CI results are recorded on PR #113 for the exact validated revision.
- The preceding input-budget fix at `b58118d293d407f89132c9a87ad73ea22d6f4569` completed all five workflows successfully: [main build](https://github.com/GnouGo/GnouGo/actions/runs/36183021331), [planner](https://github.com/GnouGo/GnouGo/actions/runs/36183020481), [frontend/Python](https://github.com/GnouGo/GnouGo/actions/runs/36183020592), [Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/36183020730), [standalone](https://github.com/GnouGo/GnouGo/actions/runs/36183020492).

Intermediate failing test/build logs and the temporary parent harness remain in local ignored artifacts. Historical evidence files and original encrypted sessions were not rewritten. The historical live result remains **8/9**. No paid inference, live benchmark, actual browser request or real Copilot task was dispatched. Real Copilot edit/test execution under the mandatory sandbox remains unverified; no policy was bypassed. This pass establishes deterministic compilation and execution behavior with mocked interpretation, not success of a new live product-search plan. PR #113 remains draft and must not be merged automatically.
