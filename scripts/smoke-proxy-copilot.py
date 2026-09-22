#!/usr/bin/env python3
"""Exercise a published ProxyCopilot binary using only synthetic local HTTP providers.

No AXA account, external LLM, database, or Python dependency is required.
Use --serve to leave the synthetic environment running for browser validation.
"""
import argparse
import base64
import http.server
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request


def encoded(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


class Provider(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    token_calls = 0

    def log_message(self, *_):
        pass

    def respond(self, value, status=200):
        body = encoded(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.endswith("/.well-known/openid-configuration"):
            self.respond({"token_endpoint": f"http://127.0.0.1:{self.server.server_port}/token"})
        else:
            self.respond({"error": "unknown route"}, 404)

    def do_POST(self):
        payload = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if self.path == "/token":
            expected = "Basic " + base64.b64encode(b"smoke-client:synthetic-client-secret").decode()
            if self.headers.get("Authorization") != expected:
                self.respond({"error": "invalid authentication"}, 401)
                return
            Provider.token_calls += 1
            self.respond({"access_token": "synthetic-oidc-token", "expires_in": 3600})
            return
        request = json.loads(payload)
        provider = self.path.split("/")[1]
        expected_header = "x-api-key" if provider == "anthropic" else "Authorization"
        expected = "synthetic-anthropic-key" if provider == "anthropic" else "Bearer synthetic-oidc-token" if provider == "axa" else "Bearer synthetic-copilot-key" if provider == "copilot" else None
        if expected and self.headers.get(expected_header) != expected:
            self.respond({"error": "invalid authentication"}, 401)
            return
        if request["model"] != "vendor/demo-model":
            self.respond({"error": "model ID changed"}, 400)
            return
        has_results = any(message["role"] == "tool" or isinstance(message.get("content"), list) and any(block.get("type") == "tool_result" for block in message["content"]) for message in request["messages"])
        tools = bool(request.get("tools")) and not has_results
        live = "Watch the stream" in encoded(request)
        if tools:
            answer = ""
        elif live:
            answer = "The response arrives as a sequence of small events. Each event is forwarded immediately to Copilot and appears here while the model is still generating. Cancelling the request closes the upstream connection, so generation does not continue in the background."
        else:
            answer = "Cancellation flows from VS Code through the proxy to the provider. The relay forwards text and tool calls incrementally, records timing and token usage, and releases the upstream connection when the request ends."
        tool = {"id": "call_read", "type": "function", "function": {"name": "read_file", "arguments": encoded({"path": "src/ProxyRelay.cs"})}}
        usage = {"prompt_tokens": 328, "completion_tokens": 64, "total_tokens": 392}
        streaming = request.get("stream", False)
        if not streaming:
            self.respond({"id": "chat-smoke", "object": "chat.completion", "model": request["model"], "choices": [{"index": 0, "message": {"role": "assistant", "content": answer}, "finish_reason": "stop"}], "usage": usage})
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson" if provider == "ollama" else "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

        def emit(value):
            text = encoded(value) if isinstance(value, dict) else value
            wire = text + "\n" if provider == "ollama" else "data: " + text + "\n\n"
            self.wfile.write(wire.encode())
            self.wfile.flush()
            if live:
                time.sleep(0.15)

        try:
            if provider == "anthropic":
                emit({"type": "message_start", "message": {"id": "msg-smoke", "usage": {"input_tokens": 328, "output_tokens": 0}}})
                emit({"type": "content_block_start", "index": 0, "content_block": {"type": "tool_use", "id": "call_read", "name": "read_file", "input": {}} if tools else {"type": "text", "text": ""}})
                if tools:
                    emit({"type": "content_block_delta", "index": 0, "delta": {"type": "input_json_delta", "partial_json": tool["function"]["arguments"]}})
                else:
                    for start in range(0, len(answer), 18):
                        emit({"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": answer[start:start + 18]}})
                emit({"type": "content_block_stop", "index": 0})
                emit({"type": "message_delta", "delta": {"stop_reason": "tool_use" if tools else "end_turn"}, "usage": {"output_tokens": 64}})
                emit({"type": "message_stop"})
            elif provider == "ollama":
                if tools:
                    emit({"message": {"role": "assistant", "content": "", "tool_calls": [{"function": {"name": "read_file", "arguments": {"path": "src/ProxyRelay.cs"}}}]}, "done": False})
                else:
                    for start in range(0, len(answer), 18):
                        emit({"message": {"role": "assistant", "content": answer[start:start + 18]}, "done": False})
                emit({"message": {"role": "assistant", "content": ""}, "done": True, "done_reason": "stop", "prompt_eval_count": 328, "eval_count": 64})
            else:
                def chunk(delta, reason=None):
                    return {"id": "chat-smoke", "object": "chat.completion.chunk", "model": request["model"], "choices": [{"index": 0, "delta": delta, "finish_reason": reason}]}
                emit(chunk({"role": "assistant"}))
                if tools:
                    emit(chunk({"tool_calls": [dict(tool, index=0)]}))
                else:
                    for start in range(0, len(answer), 18):
                        emit(chunk({"content": answer[start:start + 18]}))
                emit(chunk({}, "tool_calls" if tools else "stop"))
                emit({"id": "chat-smoke", "object": "chat.completion.chunk", "choices": [], "usage": usage})
                emit("[DONE]")
        except (BrokenPipeError, ConnectionResetError):
            pass


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def call(base, path, body=None, method=None):
    request = urllib.request.Request(base + path, data=None if body is None else encoded(body).encode(), headers={"Content-Type": "application/json"}, method=method)
    with urllib.request.urlopen(request, timeout=15) as response:
        return response.read().decode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--serve", action="store_true")
    parser.add_argument("--port", type=int)
    args = parser.parse_args()
    binary = args.binary.resolve()
    assert binary.is_file(), f"Missing binary: {binary}"
    upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Provider)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    port = args.port or free_port()
    base = f"http://127.0.0.1:{port}"
    upstream_url = f"http://127.0.0.1:{upstream.server_port}"
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("PROXYCOPILOT__")}
    env.update(URLS=base, OpenTelemetry__Enabled="false")
    for provider, kind in [("axa", "openai"), ("copilot", "copilot"), ("anthropic", "anthropic"), ("ollama", "ollama")]:
        prefix = f"ProxyCopilot__Providers__{provider}__"
        values = {"Connection__Type": kind, "Connection__Url": upstream_url + "/" + provider + ("/v1" if provider in ("axa", "anthropic") else ""),
                  "Models__code__UpstreamId": "vendor/demo-model", "Models__code__Metadata__DisplayName": provider.title() + " · Coding model",
                  "Models__code__Metadata__MaxInputTokens": "120000", "Models__code__Metadata__MaxOutputTokens": "8000",
                  "Models__code__Metadata__Capabilities__SupportsTools": "true",
                  "Connection__RequestPolicy__UnspecifiedOutputTokens": "Configured", "Connection__RequestPolicy__DefaultMaxOutputTokens": "4096"}
        if provider == "axa":
            values.update(Authentication="OidcClientSecret", Connection__Issuer=upstream_url + "/oidc", Connection__ClientId="smoke-client", Connection__ClientSecret="synthetic-client-secret", Connection__Scopes="models.read")
        elif provider in ("copilot", "anthropic"):
            values.update(Authentication="ApiKey", Connection__ApiKey="synthetic-" + provider + "-key")
        env.update({prefix + key: value for key, value in values.items()})
    process = subprocess.Popen([str(binary)], cwd=binary.parent, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    logs = []
    def collect():
        for line in process.stdout:
            logs.append(line)
    threading.Thread(target=collect, daemon=True).start()
    try:
        deadline = time.monotonic() + 20
        while True:
            if process.poll() is not None:
                raise RuntimeError("Published server exited: " + "".join(logs))
            try:
                assert json.loads(call(base, "/health"))["status"] == "ok"
                break
            except (urllib.error.URLError, ConnectionError):
                if time.monotonic() > deadline:
                    raise RuntimeError("Published server did not start: " + "".join(logs))
                time.sleep(0.1)
        assert "<html" in call(base, "/ui/")
        assert len(json.loads(call(base, "/v1/models"))["data"]) == 4
        setup = call(base, "/api/setup")
        assert "customendpoint" in setup and "synthetic" not in setup
        for provider in ["axa", "copilot", "anthropic", "ollama"]:
            messages = [{"role": "system", "content": "You are a coding assistant. Explain the code precisely."}, {"role": "user", "content": "Read the relay and explain how cancellation flows through a streaming request."}]
            request = {"model": provider + "/code", "stream": True, "stream_options": {"include_usage": True}, "messages": messages,
                       "tools": [{"type": "function", "function": {"name": "read_file", "description": "Read a file in the workspace", "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}}}]}
            wire = call(base, "/v1/chat/completions", request)
            assert "[DONE]" in wire and "tool_calls" in wire
            tool_id = None
            for line in wire.splitlines():
                if line.startswith("data: {"):
                    event = json.loads(line[6:])
                    for choice in event.get("choices", []):
                        for tool in choice.get("delta", {}).get("tool_calls", []):
                            tool_id = tool.get("id", tool_id)
            assert tool_id
            messages.extend([{"role": "assistant", "content": None, "tool_calls": [{"id": tool_id, "type": "function", "function": {"name": "read_file", "arguments": encoded({"path": "src/ProxyRelay.cs"})}}]},
                             {"role": "tool", "tool_call_id": tool_id, "content": "using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);\nawait http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);"}])
            answer = call(base, "/v1/chat/completions", request)
            assert "[DONE]" in answer and "392" in answer
        assert Provider.token_calls == 1, f"Expected one cached token, got {Provider.token_calls}"
        calls = json.loads(call(base, "/api/traffic"))["calls"]
        assert len(calls) == 8 and all(item["status"] == "completed" for item in calls)
        for item in calls:
            detail = call(base, "/api/traffic/" + item["id"])
            assert "synthetic-" not in detail
        print(f"PASS: published binary; four providers; streaming tool round trips; OIDC cache; UI; models; setup; traffic. URL={base}", flush=True)
        if args.serve:
            signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
            while True:
                time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
        upstream.shutdown()


if __name__ == "__main__":
    main()
