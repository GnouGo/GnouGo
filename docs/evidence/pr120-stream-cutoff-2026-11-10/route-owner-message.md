Subject: approved generation route — incomplete SSE near 300 seconds idle

Prepared for the route owner; **not sent**. All times below are UTC, 2026-11-10.
Endpoint coordinates and retained bytes are in the existing private packet; request
files marked DO-NOT-SEND must not be replayed.

| Provider response ID | Request window | Last received bytes |
| --- | --- | --- |
| `chatcmpl-EiTxUTNKH8FVlH9IBNhOpc7EKUemM` | 00:24:36.623705–00:30:07.991369 | 00:25:06.828760 |
| `chatcmpl-EiV1JnORDabC5pdKBQDMPGc7k2S8d` | 01:32:37.705460–01:38:05.934855 | 01:33:04.770983 |

Both returned HTTP 200 and initial Chat SSE envelopes, then ended 301.163 seconds
after the last data, with 269/272 seconds left before the client deadline.
Neither delivered finish, `[DONE]` or usage; no result was applied. Historical
records cannot distinguish clean read EOF from a network exception or locate closure.
The unchanged client completed a **loopback-only** 310-second silence control.

Please correlate these IDs across ingress, proxies and upstream generation logs:
which hop initiated closure, did framing finish, and what upstream/downstream byte
counts, terminal events, idle policies and cancellation timestamps were recorded?
Forwarded `X-Client-Request-Id` coordinates are in the
[correlation dossier](code20-code21-correlation.json); historical forwarding is unknown.

Discriminating test, requiring your authorized route access: relay synthetic SSE
through the same policy chain, initial envelopes then 310 seconds silent then
content/finish/usage/`[DONE]`; compare with the same duration and SSE comments every
30 seconds. Retain per-hop flush/close times. **This route test has not been run.**
