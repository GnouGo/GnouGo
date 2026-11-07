# Authorized-route response cutoff

The [exact safe journal extract](remote-cutoff-evidence.json) identifies five physical attempts on the unchanged deployment. The endpoint is represented by its SHA256; its full value and request bytes remain encrypted in the existing campaign records. Local attempt IDs correlate those records; they are not claimed to be headers transmitted to the remote server.

## Demonstrated boundary

- On 7 November 2026, 08:40:17–08:55:20 UTC, the three captured attempts of session `0eb17725cede426bbeddde86cc51081b`, request 17, each returned a complete HTTP 500 response after approximately 300 seconds. All bodies report `ESG120`, `Routing failed`, `Backend not available, no answer from backend.`
- At 10:01:27–10:06:27 UTC, session `8227a8c68f3a4706b69dec086ec93789`, request 11, lost transport after 300.445 seconds without a status/body/usage receipt.
- At 10:25:29–10:30:29 UTC, session `01a4e292867d4a239a96ee8d49a3473d`, request 17, lost transport after 299.876 seconds without a status/body/usage receipt.

The actual local HttpClient and captured physical-attempt limits are 600,000 ms. No 300-second request override is present. Connection pool lifetime does not terminate an active request. The complete remote 500 responses therefore establish a remote failure before the local deadline. The two transport losses alone cannot identify the peer that closed the connection. No Amazon business tool was invoked in these generations.

The exact remote owner and cause remain inaccessible. A configured 300-second gateway deadline, backend response deadline, backend failure, and schema/backend variation are hypotheses. This report does not assert that a particular product setting is currently 300 seconds.

## Required operator action

1. Correlate the exact authorized deployment, account, UTC windows and retained request-body fingerprints with gateway routing and backend logs. Recover any existing backend response/usage first. Identify which component produced ESG120, whether its backend request continued, and which component closed the later two connections. Preserve response IDs, original error bodies and usage; do not redispatch any retained request.
2. If those traces confirm a 300-second synchronous response deadline, change that deadline **on this same authorized route**, including each owning gateway/backend adapter, to at least **600,000 ms**, with any outer intermediary allowing the inner deadline to return an outcome. The client already captures 600,000 ms; changing only its timeout does not repair this boundary. The owning product's configuration key cannot be named truthfully without its configuration/log access.
3. If the synchronous route has an immutable 300-second limit, expose and explicitly authorize a compatible Responses route on the same gateway/model deployment. It must accept background creation, return a stable response ID before that boundary, and support authenticated `GET /responses/{id}` recovery and documented retention. Provide its exact approved URL, protocol and observed create/retrieve receipts. Do not bypass the gateway or substitute a model/provider.

The existing provider has background creation/polling code, but foreground durable campaign journals currently reject that mode. A verified compatible route would also require journaled creation/polling and response-ID recovery before the actual Server path could safely use it. A flag alone is insufficient.

Read-only same-prefix checks returned OPTIONS 400 `Invalid Preflight Request` and GET 404 `NotFound`. These generic responses do not verify background creation support or prove its absence. No paid background POST was made on an unverified route.

## Qualification after an operator change

Capture the explicit route/policy amendment while retaining the same original Limited campaign, costs and holds. Use a new independently identified request, never an original request replay. Verify actual HTTP settings and a durable receipt, then resume a fresh permission-aligned Agent.Server session with the unchanged original requirements, deterministic validation, business judges, delegated execution authority and independent oracle. At this handoff's original boundary only a technical checkpoint existed. The later continuation in the [qualification report](README.md) reached a business-accepted contribution, globally accepted plan, delegated approval and partial real execution; the unchanged oracle failed. Those later facts must not be conflated with the original cutoff.

## Separate later output-exhaustion evidence

Trial 10 at 13:32:57–13:37:50 UTC returned a complete HTTP 200 with `finish_reason: length`, 32,768 reasoning tokens and empty visible output on a short intent request, despite actual `reasoning_effort: low`. Its body reports roughly 286 seconds of backend generation. This is not a transport cutoff and does not prove a gateway deadline. The exact same intent request at medium effort then returned a valid response in 8.184297 seconds. Keep these traces separate from ESG120/transport losses; correlate actual backend reasoning/decoding and request handling before assigning a cause. [Exact comparison](intent-effort-comparison.json). No local timeout, token increase or unsupported reasoning mode is proposed as a demonstrated cure.
