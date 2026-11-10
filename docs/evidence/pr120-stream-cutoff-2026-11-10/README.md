# Code stream cutoff: route-owner investigation

Follow-up from verified HEAD `0719c65a`: the demonstrated diagnostic gap is
addressed in [new-attempt read diagnostics](diagnostic-followup.md), and a
[short route-owner message](route-owner-message.md) is prepared but unsent.
[Retained Amazon DOM reproduction](amazon-empty-context-reproduction.md) now
demonstrates a generic accessible-label/ARIA-heading observation defect. Its fix
does not resolve the remaining product provenance or qualify the source.
The original investigation below remains historical evidence.

Final follow-up checks: [complete local validation](followup-validation.json),
[unchanged funding and stage status](followup-status.json), and the
[independent source-only check after the Browser correction](amazon-observation-live-check.json).
The latter validates observation, but unchanged v7 still rejects product provenance.

Base HEAD: `7a12a9d88f2aef143a18bc1796fb49f734da31f8`. The controlled reproduction
was committed as `a2ffec26`. No runtime engine, provider configuration, permission,
oracle or LLM guidance changed. The [successful stored-KeyVault SDK command](../pr120-supplement-convergence-2026-11-08/copilot-keyvault-live-command-05.json)
remains the positive reference and was not executed again.

## Established facts

[Fresh journal/inspector correlation](code20-code21-correlation.json) verifies the
original Code20/21 records and their timestamps unchanged. The protected existing
inspector exported both sessions with zero provider dispatches. Its actual request
and partial-response documents match the campaign bytes exactly. These calls use
the encrypted-journal fallback; an OTel trace/span identity is unavailable.

Both calls received HTTP 200 with native Chat SSE, low reasoning, strict JSON schema,
32,768 output tokens and no native tools. Neither delivered a finish reason, `[DONE]`
or usage object. Code20 supplied only `{"`; Code21 supplied no nonempty content.
Neither supplied a usable fragment or completion receipt. Their full reservations
remain retained; old continuation and generation replay remain forbidden.

| Physical attempt | Code20, call 27 | Code21, call 52 |
| --- | --- | --- |
| Start, Paris CET | 2026-11-10 01:24:36.623705 | 2026-11-10 02:32:37.705460 |
| Last recorded bytes, Paris CET | 01:25:06.828760 | 02:33:04.770983 |
| End, Paris CET | 01:30:07.991369 | 02:38:05.934855 |
| Last byte to end | 301.162609 s | 301.163872 s |
| Client deadline still remaining | 268.632336 s | 271.770605 s |
| Request body | 122,798 bytes | 126,522 bytes |
| Provider response ID | `chatcmpl-EiTxUTNKH8FVlH9IBNhOpc7EKUemM` | `chatcmpl-EiV1JnORDabC5pdKBQDMPGc7k2S8d` |

The JSON retains exact UTC timestamps, logical request IDs, physical attempt IDs,
fingerprints and inspector keys. Host invocation duration includes preparation and
receipt recovery; it differs from physical HTTP elapsed time and post-byte silence.

The journal deliberately strips URL queries. A read-only public KeyVault configuration
read reconstructs **both original full-URI/body fingerprints**, including
`api-version=2025-01-01-preview`. The reconstructed full URI has SHA-256
`29de0adb4b5c62bdcfd463856107691df16cd2e574c064cfdc62153991ca4f87`, matching
the SDK05 reference. No endpoint mismatch was demonstrated. Historical authentication
headers and negotiated HTTP version were not captured; configuration is not proof
of what an intermediary forwarded.

## Controlled minimal reproduction

[Five real-socket controls](controlled-socket-reproduction.json) use the unchanged
HTTP factory, routing client, native reader and a cold file-journal reader against
an owned loopback HTTP/1.1 fixture. The complete response arrives after **310 seconds
of silence**, before the unchanged 600-second deadline, despite the existing
300-second connection-pool lifetime. All five controls and all **383 Core tests** pass,
without warnings or paid model calls.

```sh
GNOU_GO_NATIVE_STREAM_IDLE_SECONDS=310 dotnet test tests/GnOuGo.AI.Core.Tests \
  -m:1 -warnaserror --filter FullyQualifiedName~NativeStreamingSocketTests \
  --logger 'trx;LogFileName=native-socket-idle.trx'
```

The negative controls cover clean HTTP EOF before completion, peer socket reset,
missing `[DONE]` despite final usage, and the captured deadline. Every incomplete
result is rejected; a cold journal read cannot send it again. Default CI runs all
five controls with a short fixture delay through the existing Core suite.

**Diagnostic limit:** clean HTTP EOF and a reset both produce the broad `transport`
category. `ResponseBodyComplete=false` is set when native assembly fails; it does
not independently establish incomplete HTTP framing. The historical records cannot
identify FIN, RST, a clean upstream protocol termination, or the closing network hop.
The local unconditional idle-cutoff hypothesis was not reproduced. Direct loopback
does not reproduce corporate TLS, proxy policy or the remote generation service.

## Required route-owner investigation

The prepared private packet is
`artifacts/live-convergence-20261108/diagnosis301-route-owner/route-owner-packet.json`.
It contains endpoint/correlation details and references to retained request bytes and
partial SSE. It contains no credentials and has **not been sent**. Retained request
files are explicitly marked `DO-NOT-SEND`: they are evidence, not replay commands.

1. Locate both provider response IDs and UTC windows across the approved route's
   ingress, proxy, gateway and upstream generation logs. Correlate the physical
   attempt IDs where `X-Client-Request-Id` was forwarded; the client source sets that
   header, but historical forwarded headers are unavailable.
2. Identify which hop initiated closure and whether HTTP framing completed. Obtain
   upstream/downstream byte counts and timestamps for initial envelopes, useful
   deltas, terminal markers and usage. Check idle timers, buffering, protocol bridging
   and upstream cancellation around the common 300-second boundary. Do not infer
   the responsible component from the approximately 301-second observation alone.
3. In the owner's authorized diagnostic environment, carry synthetic SSE through
   the same policy chain: initial envelopes, 310 seconds silent, then content,
   finish, usage and `[DONE]`. Compare with the same total duration plus SSE comments
   every 30 seconds. A difference tests an idle-timer hypothesis. This requires the
   owner's infrastructure access; GNouGo's configured route remains unchanged.
4. After an evidenced delivery correction or a new discriminating observation,
   validate one fresh representative strict-schema generation. A prepared,
   **unexecuted** command uses the existing independent-probe harness and completed
   Code21 call 32, with a 122,683-byte request, low reasoning and 32,768 output tokens.
   It creates a fresh nonce/request, applies no candidate, and allows one physical
   attempt. It does not resume Code21. Recheck identity, configuration and funding
   before dispatch. Do not run it merely because the small health probe passed.

Only after representative remote delivery is verified may a fresh full Code session
start. Measure calls, wall time and tariff estimates separately to the first useful
checkpoint, global review, and independently verified execution. Code21's four
checkpoints remain accepted planning history with **no execution**. Full Code is
still **0/2**; the SDK command and loopback fixture do not change that count.

## Amazon source prerequisite and funding

The [fresh source-only check](amazon-real-profile-source-prerequisite.json) uses the
actual benchmark configuration builder and full authorized v3/v7 host allowlist,
with no narrower search-route replacement. It captured all **1,495 records** and
65 sponsored `/clk/` links; this source contains no `/clk` links. The historical
`/clk` boundary remains forbidden. Three individually complete DOM reads agree
with the observed empty `data-asin` divs without navigation roles. Whole-page HTML
was truncated and is not used to claim completeness; the observation manifest and
the three context reads are complete.

Unchanged v7 still reports `search_product_context_unverified` and offers zero
eligible advertising entries. No product visit, omission, route normalization,
permission expansion or full Amazon planning occurred. Browser close was verified.
Source acquisition alone does not exercise the delegated product-link guard or
qualify workbook execution. No generic observation defect was demonstrated.

[Initial and final funding audits](funding-and-stage-status.json) are deeply identical:
ceiling **EUR 1,700**, **EUR 463.312512332074989770532235018** available after retained
liabilities, six funding entries and 47 unresolved HTTP reservations. There was no
supplement or paid model call. Application and campaign estimates can overlap for
one physical attempt; do not add them as separate spending. Estimates and holds
are not invoices; global funding remains unavailable.
