# Draft message — not sent

Please investigate structured-output whitespace repetition on our existing
KeyVault `OpenAi` Chat Completions route, model `gpt-6.1-sol-2026-09-29`.

- Provider response ID: `chatcmpl-EioJpLTBgK7B2vmqF0UKM9kJCMZmH`.
- UTC HTTP window: **2026-11-10 22:09:25.167966–22:11:35.904634**.
- Trace: `b490faff6d8a1e5b32ab289f3060319b`; gen-ai span: `3e5a6df81ece0736`.
- Session: `a4f2be1d43ea4fa98254ea9e4704203a`, logical call 9.
- HTTP 200; streaming and native protocol completed; finish reason `length`.
  Output allocation/usage 8,192; reasoning 320; input 11,280.
- Strict `json_schema` was transmitted. Raw content has a 3,609-character
  unfinished JSON prefix followed by 24,202 whitespace characters. Compact JSON
  and no-padding guidance was already present. No retry occurred. Independent
  concatenation of 7,864 native SSE events exactly reproduces the retained text
  and its whitespace tail; the client assembler did not create it.
- Local validation accepts a manually completed suffix under both the exact
  issued and on-wire schemas. That witness was never accepted as model output.

Please confirm the actual upstream model/deployment and structured-decoder
configuration for this response. Do your logs show a decoder dead end, grammar
fallback or repeated whitespace sampled upstream? Was the content changed by
any intermediary? Where was `length` generated?

Suggested discriminating test, through this **same authorized route**: emit the
generic nested array/object/nullable type in `nested-type-reproduction.json`
under strict structured output, first with its retained recursive `$defs`, then
with nonrecursive shared definitions inlined (preserve recursive references and
the accepted language). Keep model, reasoning, transport
and output allowance fixed; preserve each exact request/response with separate
identities and reservations. Capture decoder diagnostics and native deltas to
locate the first divergence. This is a proposed test, not evidence of an executed
route experiment. No business fragment should be applied from either probe.

The exact original exchange remains in the protected inspector; request identifiers
and evidence hashes are in `diagnosis.json`. Original business content should be
shared only through the existing authorized channel if needed.
