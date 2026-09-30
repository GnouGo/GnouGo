#!/usr/bin/env python3
"""Inject one 429 before relaying to a real local proxy; never invent model output.

Run the Agent smoke against the printed URL and retry/model. Both proxy instances
use the real relay. Credentials stay in the existing target proxy. Ctrl+C stops
only this script's child process and local rejection server.
"""
import argparse
import http.server
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--target", required=True, help="Running real proxy, for example http://127.0.0.1:5098")
    parser.add_argument("--model-id", required=True, help="Model ID exported by the target proxy")
    parser.add_argument("--retry-after", type=int, default=150)
    parser.add_argument("--port", type=int)
    args = parser.parse_args()
    target = urllib.parse.urlparse(args.target)
    if target.scheme != "http" or target.hostname not in ("127.0.0.1", "localhost") or target.username or target.path not in ("", "/") or target.query or target.fragment:
        parser.error("Target must be a plain loopback HTTP origin")
    if not 0 <= args.retry_after <= 1800:
        parser.error("Retry delay must be between 0 and 1800 seconds")
    with urllib.request.urlopen(args.target.rstrip("/") + "/api/setup") as response:
        models = [model for group in json.load(response)["configuration"] for model in group["models"]]
    model = next((model for model in models if model["id"] == args.model_id), None)
    if not model or not model.get("toolCalling"):
        parser.error("Choose an available real model with tool calling")
    root = Path(tempfile.mkdtemp(prefix="gnougo-retry-smoke-", dir="/tmp"))
    state = {"injectedRejections": 0, "forwardedRequests": 0, "observedWaitSeconds": None}
    lock = threading.Lock()
    first_rejection_at = None

    class Gateway(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_):
            pass

        def do_POST(self):
            nonlocal first_rejection_at
            payload = self.rfile.read(int(self.headers.get("Content-Length", 0)))
            with lock:
                inject = first_rejection_at is None
                if inject:
                    first_rejection_at = time.monotonic()
                    state["injectedRejections"] += 1
                elif state["observedWaitSeconds"] is None:
                    state["observedWaitSeconds"] = time.monotonic() - first_rejection_at
            if inject:
                body = b'{"error":{"message":"Controlled quota rejection for retry validation"}}'
                self.send_response(429)
                self.send_header("Retry-After", str(args.retry_after))
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                print(f"Injected HTTP 429; Retry-After: {args.retry_after}", flush=True)
                return
            if state["observedWaitSeconds"] < args.retry_after:
                self.send_error(500, "Proxy retried before the advertised deadline")
                return
            request = urllib.request.Request(args.target.rstrip("/") + "/v1/chat/completions", data=payload,
                                             headers={"Content-Type": "application/json"})
            try:
                try:
                    response = urllib.request.urlopen(request, timeout=1200)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    self.send_response(response.status)
                    self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
                    if response.headers.get("Retry-After"):
                        self.send_header("Retry-After", response.headers["Retry-After"])
                    self.send_header("Connection", "close")
                    self.end_headers()
                    self.close_connection = True
                    with lock:
                        state["forwardedRequests"] += 1
                    while chunk := response.read1(4096):
                        self.wfile.write(chunk)
                        self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass

    gateway = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Gateway)
    threading.Thread(target=gateway.serve_forever, daemon=True).start()
    port = args.port or free_port()
    env = {key: value for key, value in os.environ.items() if not key.startswith(("ProxyCopilot__", "Urls", "URLS", "ASPNETCORE_", "DOTNET_ENVIRONMENT"))}
    env.update(DOTNET_ENVIRONMENT="Production", URLS=f"http://127.0.0.1:{port}", OpenTelemetry__Enabled="false")
    settings = {
        "Capture__MaxBodyBytes": 4194304,
        "Providers__retry__Connection__Type": "openai",
        "Providers__retry__Connection__Url": f"http://127.0.0.1:{gateway.server_port}/v1",
        "Providers__retry__Connection__RetryPolicy__MaxAttempts": 10,
        "Providers__retry__Connection__RetryPolicy__MaxTotalDelayMilliseconds": 1800000,
        "Providers__retry__Models__model__UpstreamId": args.model_id,
        "Providers__retry__Models__model__Metadata__MaxInputTokens": model["maxInputTokens"],
        "Providers__retry__Models__model__Metadata__MaxOutputTokens": model["maxOutputTokens"],
        "Providers__retry__Models__model__Metadata__Capabilities__SupportsTools": "true"
    }
    env.update({"ProxyCopilot__" + key: str(value) for key, value in settings.items()})
    stopped = threading.Event()
    signal.signal(signal.SIGINT, lambda *_: stopped.set())
    signal.signal(signal.SIGTERM, lambda *_: stopped.set())
    process = None
    try:
        with (root / "proxy.log").open("w") as log:
            process = subprocess.Popen([str(args.binary.resolve())], env=env, stdout=log, stderr=subprocess.STDOUT)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError(f"Test proxy failed to start; inspect {root / 'proxy.log'}")
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1):
                        break
                except urllib.error.URLError:
                    stopped.wait(.1)
            else:
                raise RuntimeError("Test proxy did not become ready")
            print(f"READY: http://127.0.0.1:{port}; model: retry/model; evidence: {root}", flush=True)
            while not stopped.wait(.5):
                if process.poll() is not None:
                    raise RuntimeError("Test proxy exited unexpectedly")
    finally:
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        gateway.shutdown()
        gateway.server_close()
        (root / "retry-evidence.json").write_text(json.dumps(state, indent=2) + "\n")
        print(json.dumps(state), flush=True)


if __name__ == "__main__":
    main()
