# Pricing and reasoning validation

Validated on macOS ARM64, 25 September 2026, with .NET SDK 10.0.300, VS Code 1.139.0, and bundled Copilot 0.67.0.

## Automated checks

- Proxy tests: 162 passed, including tier boundaries, cumulative usage, missing prices/usage, explicit zero rates, cache/reasoning subsets, interrupted native streams, currency separation, retention, reconnection, and reasoning defaults.
- AI.Core tests: 222 passed, including external cache-write pricing, overrides, and cloning.
- Frontend tests: 4 passed; TypeScript/Vite build passed without warnings.
- Component and full solution builds: no warnings or errors.
- Standalone `osx-arm64` Native AOT publish: no warnings or errors; private settings excluded.
- Published-binary HTTP smoke: all four adapters, streaming tool loops, OIDC caching, source-generated tier binding, reasoning defaults, and separate currency totals passed.
- Published-binary browser smoke: incremental output, translated payloads, precise estimates, filters, retained-history totals, pause/resume, reconnection, clearing, and mobile layout passed without browser errors. See [estimated costs](screenshots/estimated-costs.png).

The HTTP/browser smoke uses synthetic local providers. It does not establish real-provider acceptance by itself.

## Real desktop checks

The two existing workstation profiles were synchronized with `/api/setup`. Their six model entries and standard-picker settings were verified against the generated configuration. To avoid interrupting existing editor windows, automated desktop checks used isolated copies of each profile's selected model configuration in the installed VS Code application.

Both configured reasoning models passed in both profile configurations: the visible Thinking Effort control offered None, Low, Medium, High, and Extra High, initially selecting None. Selecting High and sending Ask's `/explain` command produced a successful real-provider response with `reasoning_effort: high`, no function tools, the expected arithmetic answer 437, and a EUR estimate. Screenshots and provider evidence remain private.

The separate real Agent smoke passed with None: nine streamed model turns, workspace inspection, file creation/read/edit, shell execution, explicit terminal-output retrieval, and a parallel read turn. VS Code executed all tools. The model used the observed result 42 and a freshly generated nonce to write and verify its result file. No tool-call fixtures were used.

## Current Copilot limitation

Copilot 0.67 sends tools in ordinary Ask mode and injects `session_store_sql` even after visible tools are deselected or a custom agent declares `tools: []`. Disabling local indexing did not remove that injected tool in these checks. The tested gateway rejects higher reasoning with function tools on Chat Completions. The reasoning selector is correctly configured; ordinary Ask and Agent should use None for these deployments. The tested text-only `/explain` route accepts High. Responses support is outside this change.

Reproduce the checks using the [component README](../README.md#test-and-publish) and the smoke scripts. Private endpoints, credentials, model aliases, pricing provenance, and screenshots of real configurations are not included here.
