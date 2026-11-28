# Ready to transmit — not sent

Please correlate provider request `chatcmpl-Ej0Gll9fLdj7uID98paeWmZ1PWzmF`
through the existing KeyVault OpenAi route. This independent Amazon consumer-draft
probe used the unchanged model, streaming protocol and 600-second client deadline
on source `926b8131`. It made one attempt; no partial result was applied.

- Request began **2026-11-11 10:54:39.353824 UTC**.
- HTTP 200, HTTP/1.1; first and last data **10:55:11.039798 UTC**.
- Two initial SSE envelopes, zero useful content, no finish marker or usage.
- Read exception **11:00:11.714987 UTC**: `io`, `ConnectionReset`.
- Attempt ended **11:00:11.716384 UTC**, **300.676586 seconds** after last data.
- Client deadline remained **11:04:39.353824 UTC**. No retry was dispatched.

Which component initiated the TCP reset, and why? Please correlate gateway ingress,
upstream provider request, last upstream byte, cancellation/disconnect events and
configured idle limits in **10:54:39–11:00:12 UTC**. Did the provider continue
generating after the downstream reset, and is a final response or usage recoverable?
Compare with earlier Code request `chatcmpl-EiqPJzJgPETp2KBo68QshMxFlLtsA`
(**2026-11-11 00:22:49–00:28:19 UTC**). The client identifies a network exception;
it cannot locate the resetting component.

The discriminating test needs an authorized endpoint through this same route:
one stream silent for 310 seconds before valid completion, compared with one
sending periodic SSE comments during the same wait. Correlate both directions.
Only the historical local HTTP control has passed; this route test has **not**
been performed. Route access remains unavailable. This message is prepared only;
no message has been sent and no network workaround is authorized.
