# Provider budget verification — 2026-09-20

Historical preflight: the user subsequently waived this prerequisite. See the
[resumed native Desktop test](planning-desktop-protocol-2026-09-20.md). The observations below
remain evidence of what was and was not verified; they no longer block the authorized test.

## Outcome

The provider-only route remains **blocked before paid execution**. The configured service is an OIDC-authenticated internal gateway; no accessible administrative interface or authoritative contract was found that demonstrates an isolated, hard EUR 20 allowance covering both planner and Copilot inference. This is an unverified capability, not a finding that the gateway cannot support it.

Inspected revision: `79702a9cd97c687619d3c5add04faae637a4fd56`, on `feat/deterministic-planner-v2`. Planner behavior remains frozen at `65dc34a`. This follows the [Desktop budget preflight](planning-desktop-e2e-2026-09-20.md) and implements its provider-control investigation only. No planner, public API, persistence, credential or provider configuration changed. No proxy was implemented.

## Verified configuration and access

Configuration was read through the public KeyVault catalog API and the existing Copilot configuration overlay. Persisted host defaults were read through `IUserConfigRepository.GetAsync`, using the existing Agent MCP persistence registration. There were no direct SQL queries, schema updates, provider authentication exchanges or secret values written to files or logs.

| Check | Result |
| --- | --- |
| Persisted host provider/model | `OpenAi` / `gpt-5.5-2026-04-24` |
| KeyVault provider | `OpenAi`, wire type `openai`, model `gpt-5.5-2026-04-24`, authentication `oidc` |
| Provider endpoint | Custom internal gateway; not the public OpenAI API. Hostname and authentication details omitted. |
| Copilot selected provider | `OpenAi`; provider configuration supplies the same model. |
| Copilot persisted reasoning | `high`, unchanged. No inference was issued. |
| Configured documentation, management and usage URLs | None found in the provider configuration. |
| Configured Copilot policy proxy | Absent. |
| Shared isolated billing scope | Not verified. Selecting the same provider does not establish this. |
| Administrative authority to set a spending cap | Not established by the available configuration; inference credentials are not proof of administrative permission. |
| Hard cap, automatic replenishment and authoritative usage records | Not verified. |

The packaged host model is only a default; persisted host settings and KeyVault select the model shown above. No switch to a public OpenAI account, alternate provider or model was made.

Two anonymous metadata GETs, for the configured gateway origin's `/` and `/openapi.json`, were attempted and then repeated when confirming persisted defaults. All four ended before an HTTP status was returned: `HttpRequestException`, `HttpRequestError.ResponseEnded`, inner `HttpIOException`. They sent no authentication or inference body. No redirects were followed and TLS validation was not disabled. These results do not establish an authentication failure, a missing endpoint or provider-wide unavailability; documentation could not be retrieved through these probes.

Public searches did not locate an authoritative spending-cap or billing-management contract for this gateway. AXA's [official Secure GPT announcement](https://www.axa.com/en/press/press-releases/axa-offers-securegenerative-ai-to-employees) describes an internal service built on Azure OpenAI, but does not specify monetary enforcement or current administrative APIs. OpenAI-compatible request syntax is not evidence that public OpenAI billing controls apply to this gateway.

No budget-exhaustion inference test was attempted: there is no verified zero-budget client or documented nonbillable rejection test yet. Existing credentials and billing settings were left untouched rather than assuming that an inference request would be free.

## Exact prerequisite to unblock

The gateway administrator needs to provide an internal management/documentation URL and an isolated client or billing scope with all of the following verifiable properties:

1. A hard maximum of EUR 20 for this additional validation, shared by planner and Copilot inference, independent of historical benchmark charges and unrelated callers.
2. Admission-time enforcement covering concurrent calls and retries, with no delayed threshold overshoot, automatic replenishment or reset during the validation.
3. Durable authoritative spending/usage records, including unresolved charges after interrupted requests.
4. A documented nonbillable rejection check or isolated zero-budget client that can prove rejection before normal inference is enabled.
5. Compatibility with the existing model and OIDC integration, with no fallback to an uncapped identity.

The user was asked for the internal administration/documentation URL only; no credential was requested in chat. No message was sent to administrators or other third parties. A suitable request for the service owner is:

> Please provide an isolated SecureGPT OIDC client or billing scope for GnOuGo validation, with an enforced EUR 20 total allowance shared by planner and Copilot calls, no replenishment, authoritative usage records and a documented nonbillable budget-rejection test. Please confirm how concurrent and retried requests are bounded and how uncertain charges remain accounted for. Credentials must be provisioned through the existing encrypted configuration channel.

Once supplied, verify the actual enforcement and route both inference paths to that same scope through existing configuration before resuming the Desktop plan. If the provider offers only alerts, delayed enforcement or unverifiable spending, this route cannot meet the approved requirement. Do not add a new proxy or relax the ceiling within this task.

## Validation and effects

- The temporary configuration probe built and ran successfully in Release with `SkipModelMetadataGeneration=true`; no warnings or production source changes were observed.
- Delivery checks cover local document links and `git diff --check`. The full solution suite was not rerun for this documentation-only delivery.
- Model calls, repairs and paid verification calls: **0**. Additional inference tokens and spending: **0 / EUR 0**, because no inference was dispatched.
- No new planning/execution session, campaign reservation, receipt, review draft, clone or review workspace was created. Normal KeyVault read-audit entries may have been appended.
- No Desktop launch, workflow generation, command execution on SmartGuide or GitHub publication occurred. No SmartGuide source, metadata, merge or deployment changes occurred.
- Previous benchmark ledgers and stopped sessions were not resumed, reset or replaced. The complete real Desktop acceptance criteria remain pending.
