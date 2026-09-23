# VS Code Agent protocol and local acceptance

## Inspected client

The implementation was checked against the installed desktop **VS Code 1.138.0**, with bundled **Copilot Chat 0.66.0**, on macOS arm64. The inspected extension is `extensions/copilot/dist/extension.js` under the installed application's `Resources/app` directory. Its SHA-256 is `79c5b462f5c0b679051e4fefacbd82e3dbef6ad31cfaf890840f2ec85faf1e6f`.

Inspection covered the registered `customendpoint` model schema, the Chat Completions message converter, the streaming function-call accumulator, and the subsequent tool-result conversion. These observations were then checked against actual requests captured while the installed editor ran Agent tools through the real configured provider. The [VS Code model configuration documentation](https://code.visualstudio.com/docs/agent-customization/language-models) describes the user-facing Custom Endpoint integration.

The relevant wire contract is:

| Direction | Observed behavior |
|---|---|
| VS Code → proxy | `POST /v1/chat/completions`, full `messages`, `stream: true`, function `tools`, optional generation settings |
| Proxy → VS Code | SSE `choices[].delta.tool_calls[]`, stable call IDs, function names and incremental JSON argument strings; `finish_reason: "tool_calls"`, then `[DONE]` |
| VS Code execution | Built-in `list_dir`, `create_file`, `read_file`, `replace_string_in_file`, `run_in_terminal`, and `get_terminal_output` |
| Next VS Code turn | Assistant message containing the calls, followed by `role: "tool"` messages with matching `tool_call_id` values; original conversation and tool schemas retained |

The client sends `temperature: 0.1` even for the configured models that do not support temperature. The proxy omits it according to metadata. The inspected client omits an output-token field in these requests; the proxy uses its explicit provider output policy and emits `max_completion_tokens`. The real acceptance runs used a provider default and cap of 8192 tokens. The editor's output reservation controls its prompt budget and does not replace the proxy's upstream generation policy.

## Corrected incompatibility

The installed streaming accumulator looks up a call by ID when an ID is present. Otherwise it appends arguments to the last call; it does not use the supplied tool index. It also replaces the function name when another name fragment arrives. Thus a legal stream that interleaves argument fragments for calls 0 and 1 can silently mix the calls. Simply repeating IDs on every fragment would break other consumers that concatenate ID strings.

`ToolCallStream` emits IDs and assembled names once and keeps each call's argument fragments contiguous. It streams text and the active function's arguments while buffering overlapping calls until the active argument object is complete. The buffered calls remain part of the same assistant turn, so VS Code retains their parallel identities and scheduling. This normalization applies after every protocol adapter, preserving the original upstream capture separately. It never invokes tools or changes their argument values.

Regression tests reproduce the inspected consumer's behavior and independently check an index-based, concatenating consumer. They require delivery of partial arguments before the upstream finishes, then return out-of-order parallel tool results and complete the next turn. Tests also cover split function names, identity-only chunks, nested/escaped JSON, usage after finish, invalid/incomplete calls, and bounded buffering. Existing tests exercise all four adapters and cancellation/transport failures.

The setup generator now explicitly advertises tool calling and find/replace editing tools for tool-capable models, the configured context window, and a practical output reservation. No model-name detection, hidden Agent prompt, proxy-side shell, file executor, or custom GnOuGo tool server is involved.

## Reproduce the real test

Follow the [README's real desktop smoke instructions](../README.md#real-desktop-vs-code-agent-smoke). The test script launches an isolated desktop editor profile, selects the actual configured Custom Endpoint model, and submits a task through the Chat UI. Ordinary VS Code permission prompts remain in place.

The workspace begins empty. Only VS Code Agent tools create or modify its test files and execute the Python command. The test driver reads artifacts and traffic to verify the result; it never supplies tool results or model responses. Python generates a fresh random nonce in the actual editor terminal. Passing requires the model to read that nonce through `get_terminal_output`, write it into `agent-result.json`, and use it in its final answer. The verifier also checks edited file contents, matching call identities across turns, completed tool results, streaming, and parallel calls.

All provider endpoints, authentication settings, model identifiers, raw prompts, and unredacted request bodies remain local. Only reviewed screenshots and an allowlisted evidence report are suitable for publication. The normal VS Code profile is left untouched.

## Acceptance result

Both configured model aliases passed on **2026-09-22**, using the standalone **osx-arm64 Native AOT** server, real OIDC client-secret authentication, and ordinary VS Code Agent tools. Model identifiers and connection settings are intentionally omitted from these public review artifacts. These archived receipts establish tool execution for the selected aliases; they do not prove that a gateway with a fixed deployment URL honored the request body's model selection.

| Real run | Model turns | Turns with parallel calls | Observed result | Observed nonce | Evidence |
|---|---:|---:|---:|---|---|
| Configured model A | 8 | 2 | 42 | `44319b3f5dc2ee7a` | [Report](acceptance/model-a.json), [editor screenshot](screenshots/vscode-agent-model-a.png) |
| Configured model B | 9 | 1 | 42 | `8af802610e43152f` | [Report](acceptance/model-b.json), [editor screenshot](screenshots/vscode-agent-model-b.png) |

Each run executed `list_dir`, `create_file`, `read_file`, `replace_string_in_file`, `run_in_terminal`, and `get_terminal_output`. The file changed from `stage=created / value=6` to `stage=edited / value=7`. The model then created `agent-result.json` from the observed Python result and random nonce, read both files in parallel, and completed a final answer using that nonce. Every tool call had a matching result on a subsequent model request. No proxy-side or test-driver tool execution was used.

The selected configuration was **Agent → Local → GnOuGo smoke model**, `vendor: "customendpoint"`, `apiType: "chat-completions"`, `toolCalling: true`, `editTools: ["find-replace", "multi-find-replace"]`, context 128000, input ceiling 120000, output reservation 8192, full local URL `http://127.0.0.1:15087/v1/chat/completions`, and **Default permissions**. The proxy's output default/cap was 8192 and its per-body capture limit was 4 MiB for verification. The normal application port remains 5087.

These results are separate from the synthetic provider/UI smoke tests used in CI. Validation also passed: 91 proxy tests, 8 Auth.Core tests, 222 AI.Core tests, 2 frontend tests, warning-free component/solution and frontend builds, a warning-free Native AOT publish, published HTTP/UI checks, and the dashboard browser smoke. Publishing excluded the local development configuration; starting the binary with public defaults exposed no models.

## Deployment URL routing validation

The subsequent routing fix supports `{model_name}` in `Connection.Url`, resolving the selected `UpstreamId` into the deployment path before appending the protocol endpoint. Changing only the JSON `model` field cannot select another deployment when a gateway routes by URL.

Validation passed with 114 proxy tests, including concurrent requests to distinct deployments, URL encoding, API versions, startup rejection of malformed templates, larger model budgets, and streamed parallel tool/result round trips through all four adapters. The solution, frontend, and Native AOT publish completed without warnings, and the published HTTP/UI smoke verified a templated deployment URL with OIDC authentication. Three separate real configured deployments each returned HTTP 200, the requested short text, and a completed SSE stream through the published binary. These last checks verify real deployment connectivity; they do not repeat the desktop Agent task or stress-test the maximum context window.

## Reasoning and tool compatibility regression

On **2026-09-23**, the installed editor sent a real Agent request with function tools and `reasoning_effort: "medium"`. The configured gateway rejected this combination on Chat Completions with HTTP 400, despite accepting reasoning in text-only chat. Explicit `high` with tools also failed for both affected deployments. Its error required `none` or use of the Responses API. The gateway separately rejected an advertised `max` level even without tools. This was a capability configuration mismatch; a successful plain-chat request does not establish compatibility with Agent requests.

The affected private provider and VS Code entries now advertise only `["none"]` for this Chat Completions integration. Another configured deployment accepted tools with `high` and retains its supported levels. No provider/model name is embedded in routing or validation. The proxy rejects selections outside an explicitly configured list before dispatch, and the dashboard displays the already-redacted provider explanation. Supporting higher reasoning together with tools on these deployments requires a future Responses adapter; the proxy does not silently downgrade requests.

Both affected deployments then passed the full real editor task again, using their distinct templated deployment URLs, OIDC, the development server on port 5087, and **Agent → Local → Thinking Effort: None → Default permissions**. The receipts record `none` on every model turn. Each test created/read/edited a file, executed a Python command in the actual VS Code terminal, retrieved its random nonce through `get_terminal_output`, wrote a result from that observed output, and verified files with parallel calls.

| Real run | Model turns | Result | Observed nonce | Evidence |
|---|---:|---:|---|---|
| Configured model A | 9 | 42 | `697d26ac10a137eb` | [Report](acceptance/reasoning-model-a.json) |
| Configured model B | 9 | 42 | `82f67c83cb037654` | [Report](acceptance/reasoning-model-b.json) |

Validation also passed: 135 proxy tests, three frontend tests, the frontend build, a live dashboard browser check showing the actual gateway rejection, and a warning-free osx-arm64 Native AOT publish with the four-provider published-binary HTTP/UI/tool-loop smoke test. Those published-binary checks use synthetic providers; the editor tests above use the real configured gateway. Workstation settings and connection identifiers are excluded from these public receipts.
