Subject: third incomplete HTTP 200 SSE response near 301 seconds idle — ResponseEnded observed

Prepared for the route owner; **not sent**. Endpoint coordinates and request bytes
remain in the private campaign packet. Do not replay any stopped request.

New correlation, all times UTC on **2026-11-11**:

- Provider response: `chatcmpl-EiqPJzJgPETp2KBo68QshMxFlLtsA`.
- Session: `4fee0f5c8fbc42cdad72507ef1aa81a1`, Code24 call 42.
- Request: `4fee0f5c8fbc42cdad72507ef1aa81a1:42:4419b7daffd2a7695014d8ea30f537292d8d9aed8a6c336dddb10959484c3673:df99e485ef94c472b6b11889ca00a5646b8a7ac368392f003b4b0f1fabf86819`.
- Request window: **00:22:49.842690–00:28:19.461433**.
- Last received data: **00:23:18.389790**; read exception: **00:28:19.459742**.
- HTTP 200, HTTP/1.1; two initial SSE events, 745 retained bytes; zero useful text,
  no finish, `[DONE]`, or usage. One physical attempt; no fragment applied.
- Existing response-stream observer: `exception / http_io / ResponseEnded`.
  Termination was 301.071643 seconds after the last data, with 270.381257 seconds
  left before the unchanged 600-second client deadline. This was not observed as
  clean EOF or local cancellation. The initiating component remains unknown.

Please correlate this provider ID and the request window across ingress,
intermediaries and upstream generation. Which hop first closed the response?
Did HTTP framing finish? What were the per-hop byte counts, flush/close times,
upstream completion/usage and cancellation signals? Which idle policies applied?

Discriminating test requiring your authorized access: through the **same route and
policy chain**, send initial SSE envelopes, remain silent for 310 seconds, then
send content/finish/usage/`[DONE]`. Compare with the same duration and periodic SSE
comments every 30 seconds. Retain upstream and downstream flush/close observations.
This route test has **not** been run. The earlier successful 310-second control
was loopback only and was not repeated here. No network bypass is proposed.

The two older IDs/windows remain in the [original message](../pr120-stream-cutoff-2026-11-10/route-owner-message.md).
The [new sanitized evidence](code24-stream-cutoff.json) agrees with the existing
protected inspector export. That inspector uses encrypted-journal evidence here;
it supplies no telemetry trace ID. No new inspector or diagnostic mechanism was added.

Resume gate: identify/correct or disprove the route boundary with the controlled
comparison, then validate a fresh representative complete response before another
full Code qualification. Preserve stopped identities and all uncertain reservations.
