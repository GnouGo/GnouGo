# Planning HTTP 400 diagnosis and correction — 2026-09-25

This maintenance pass starts at `60d7095e7887597518e2119f9e78f56eb59c560e` on `feat/flow-hybrid-planning-v9`, for issue #112 / draft PR #113. It preserves TaskPlan planning format 10, execution journal schema 9 and the existing pipeline. No benchmark or workflow execution was authorized or performed.

## Confirmed cause

Designer session `102cd04250554a948457160432c44d07` stopped after two requests with the same content fingerprint. Both received HTTP 400. The planner discarded the typed failure and reported `MODEL_DISPATCH_UNVERIFIABLE`; Designer consequently offered an inappropriate unchanged retry. The displayed EUR 0.29 was conservative recovery accounting, not confirmed provider billing.

One separately authorized diagnostic inference attempt reproduced the rejection with the retained prompt/schema, configured gateway, `gpt-5.5-2026-04-24`, medium reasoning and the original 8,192 output-token ceiling. Its response was:

> Invalid JSON schema: regex lookaround is not supported. Found at $['$defs'].identities.items.pattern.

The response identified `invalid_json_schema`, `invalid_request_error` and `response_format`. The identity hardening introduced in `232ce5f` used lookahead, which local .NET validation accepted but the provider rejected before generating a TaskPlan. This is a response-schema compatibility defect, unrelated to MCP tool execution or the Copilot sandbox.

## Bounded diagnostic evidence

- Logical identity: `planning-http400-20260925-8d3a1612791041cf8fa731f27e7a981a`.
- Physical identity: `planning-http400-20260925-8d3a1612791041cf8fa731f27e7a981a:cfb777e89f154b0494d9bef5f9664ce7`.
- Exactly one physical inference HTTP attempt, status 400; no retries, follow-up generation or new workflow session.
- Conservative reservation: EUR **0.2935148645093396474611944225**, below the authorized EUR 0.50 ceiling. Input allowance: 17,787 tokens (serialized request bytes); output allowance: 8,192 tokens. No usage receipt exists; the reservation remains retained and is not a claim of actual billing.
- Original schema SHA-256: `65DFCDABA5600E62281D74F62E790C882B748A91FB0A9FA6238242D7B7413B9D`.
- Original encrypted request payload SHA-256: `CC8DA348CA396DE5B0E8D7DBD193F5F69BF31DCBD0B68C1F13EC0014F7DBDAE5`.
- Original session revisions, requests, receipts and budget records were hashed before/after and remained unchanged. The historical `flow-v9-112` campaign was not used or modified.
- Request/configuration/reservation and raw HTTP response are retained through the existing KeyVault record API in tenant `default`, collection `planning-http400-diagnostic-v1`. The HTTP record uses the logical identity plus `:http`. No credentials, user prompt, completion or raw provider body were written to ordinary logs.

The first harness preparation stopped **before HTTP dispatch** because the journal guard rejects a raw background-mode flag. The configured provider already selected foreground Chat Completions. The diagnostic dispatch copy was aligned with that effective protocol; its wire builder does not read that flag. The same reserved identity, schema, prompt, settings and ceiling were retained. The failed preparation and both harness snapshots remain under `artifacts/planning-http-400-2026-09-25/`; no replacement identity or second inference attempt was created.

## Changes and deterministic evidence

- Retain provider-neutral failure kind, HTTP status, retryability and allowlisted provider code in planning diagnostics and traces. Never forward the exception message or raw response body. Preserve `invalid_json_schema` through the existing provider classifier.
- Report permanent rejections as `MODEL_REQUEST_REJECTED`; block the host retry command after recovery as well as the Designer retry action. Unknown outcomes retain the existing conservative recovery behavior. Rejections do not reset reservations or invent zero usage.
- Replace lookahead with explicit lexical alternatives and `\z` for absolute end-of-input. The replacement compiles in .NET's nonbacktracking engine and RE2, whose [documented syntax](https://github.com/google/re2/wiki/Syntax) supports `\z` and excludes lookaround. No provider-name rules were added. Exact compiler identity checks are unchanged.
- Two schema regressions failed before the correction and pass afterward. Existing unsafe-ID tests remain unchanged, including trailing newline, reserved prefix, separators and Unicode. All eight deterministic business scenarios, repair/approval and recovered-session protections remain intact.
- All **228 planner tests** and **224 AI-provider tests** pass. The full solution has **2,968 passing tests** and **5 disabled opt-in live tests** across 33 test projects, with warnings treated as errors. Python: **287 core + 27 CLI + 4 script tests**, all passing.

Local detailed logs are under `artifacts/planning-http-400-2026-09-25/`. Publication, Native AOT, encrypted-persistence and exact final-revision CI results are recorded in PR #113 once completed. Historical evidence was not edited.

## Limits and recovery guidance

The corrected request was not submitted to the provider: the authorization allowed one diagnostic inference only. Deterministic checks establish removal of the confirmed unsupported construct; they do not establish end-to-end live planning success or exclude a subsequent provider rejection.

Rebuild/restart Agent.Server or its Desktop host to load the fix, then create a new planning session. The failed session still contains the original schema and accounting; do not reset or silently replay it with rewritten request content.

Historical live benchmark correctness remains **8/9**, with no new performance claim. Real Copilot command execution remains unverified because the available environment lacks the required sandbox policy. No policy bypass or permission expansion was attempted. PR #113 remains draft and must not be merged by this pass.
