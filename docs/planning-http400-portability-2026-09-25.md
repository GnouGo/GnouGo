# Planning HTTP 400: portable identity patterns — 2026-09-25

This follow-up starts at `1231db7d6e1f19cc6b8a28a8fe8d8bbf335d4cfc` on `feat/flow-hybrid-planning-v9`, for issue #112 and draft PR #113. It preserves the TaskPlan architecture, planning storage format 10 and execution journal schema 9. The [earlier diagnosis](planning-http400-fix-2026-09-25.md) remains historical evidence; its replacement pattern was not yet verified with the provider.

## Confirmed cause

Designer session `7d2f985bd33245c0a1ae9863cad8c053`, trace `20b888a4502d37e333d1ca4280a4bd05`, already used the replacement schema ending in `\z`. It stopped after one HTTP 400, so restarting the same code could not resolve the incompatibility.

One separately authorized diagnostic reproduced the rejection. The response identified `response_format`, `invalid_request_error` and the exact escaped identity expression, describing it as **not a 'regex'**. It supplied no provider error code. The prior expression compiled in .NET and RE2 but failed JavaScript's Unicode regex parser with `Invalid escape`.

The model did not reach TaskPlan generation. MCP execution and Copilot sandbox policy are unrelated to this rejection.

## Minimal correction and regression

- Replace the wire pattern's engine-specific `\z` with ordinary `$`. Keep explicit ASCII alternatives, the reserved-prefix exclusion and the compiler's exact character validation unchanged.
- Test every generated schema pattern through the existing Jint JavaScript Unicode parser as well as .NET's nonbacktracking engine. No dependency or production JavaScript validation layer was added.
- Preserve all invalid-identity cases. Their wire-pattern assertions now use JavaScript semantics; compiler and repair assertions remain unchanged. A .NET `$` match can admit a final newline, so it is not an authoritative identity check. Expanded recovery and approval tests prove that such declarations are still rejected without replacing the baseline, selections, receipts or budgets.
- Add a mocked transport regression for the retained code-less HTTP 400. It remains nonretryable, makes one request and exposes neither the echoed schema nor credentials. Existing allowlisted provider-code propagation remains unchanged; no code is invented when the provider supplies none.

The JavaScript schema regression failed at the original `\z` expression before the production edit. The corrected pattern passes JavaScript Unicode and ordinary Node.js syntax checks for 10 valid and 14 invalid boundary IDs. Historical reports and independent business oracles are unchanged.

## Bounded diagnostic evidence

- Logical identity: `planning-http400-amazon5-20260925-c8bba57a649e46869ce2bf4b4ebc1fd6`.
- Physical identity: `planning-http400-amazon5-20260925-c8bba57a649e46869ce2bf4b4ebc1fd6:40703d995ad6434593e64273861cdaae`.
- Exactly **one physical inference attempt**, HTTP 400, with no retries, follow-up inference or workflow/tool execution.
- Original model `gpt-5.5-2026-04-24`, medium reasoning, strict original schema and 8,192 output-token ceiling. The configured foreground Chat Completions protocol is unchanged.
- Conservative input allowance: **17,298 tokens** from serialized request bytes. Conservative reservation: **EUR 0.2913706919231781110234148908**, below the separately authorized EUR 0.50 ceiling. No usage receipt exists; this reservation is retained and is not a claim of actual billing.
- Original schema SHA-256: `DD743E6DC5B8DBC753F8A54C0B3FA702B85F60619E7F055D1A54B456EC6C06E1`.
- Original encrypted request payload SHA-256: `29C3ECB16F8522932F79A0A090A6FF5E2445FC2EAF21917D3A4C7AF648841A96`.
- Response body SHA-256: `1E24C340AC40EAB5FDA8203EEDA7E76FCAEF89ABA0DA30ECD0AE5F01F606A224`.

Request, configuration, reservation and raw response are encrypted through the public KeyVault record API in tenant `default`, collection `planning-http400-diagnostic-v1`, under the logical identity and its `:http` suffix. The original session revisions, requests, receipts and budget records were hashed before and after and remained unchanged. The previous diagnostic identity and the `flow-v9-112` campaign were not reused or modified.

The harness snapshot/hash, admission self-test, sanitized diagnostic output, failing/passing regression logs and deterministic validation results are retained under `artifacts/planning-http400-amazon5-2026-09-25/`. Initial local harness/test preparation errors are also retained; neither caused an inference dispatch.

## Validation and rollout

The implementation is commit `64c7b27`. The full deterministic .NET suite passed **2,972 tests** across 33 projects, including **231 planner**, **225 AI-provider** and **406 Agent.Server** tests, with warnings treated as errors and no compiler warnings. Five opt-in live tests stayed disabled. Documentation links and whitespace checks passed. Final-revision CI results are recorded in PR #113; required checks must complete on the published revision, and earlier green CI is not evidence for this correction.

Rebuild/restart Agent.Server or Desktop with the corrected revision and create a **new planning session**. Do not mutate or silently replay the stopped session's retained request. Its accounting remains intact.

The corrected request was not sent live: the single authorized inference reproduced the failure. Deterministic validation establishes the targeted compatibility correction, not end-to-end live planning success or the absence of another provider rejection. No further model request or paid benchmark is authorized by this report.

Historical live correctness remains **8/9**. Real Copilot command edit/test execution remains unverified under mandatory sandbox enforcement; no bypass or permission expansion was attempted. Keep PR #113 draft and do not merge.
