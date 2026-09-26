from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError

import pytest

from gnougo_flow_core import WorkflowRunClient


@pytest.fixture
def host():
    run = {"schemaVersion": 9, "tenantId": "tenant-a", "runId": "run-1", "revision": 7,
           "status": "needs_reconciliation", "invocations": {"main/loop/2/task": {"status": "dispatched"}}}
    state = {"run": run, "requests": [], "status": 200}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            self.handle_request()

        def do_POST(self):
            self.handle_request()

        def handle_request(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            payload = json.loads(body) if body else None
            state["requests"].append((self.command, self.path, payload))
            status = state["status"]
            if payload is not None and payload["expectedRevision"] != state["run"]["revision"]:
                status = 409
            self.send_response(status)
            if status == 302:
                self.send_header("Location", "http://127.0.0.1:1/elsewhere")
            self.end_headers()
            if not self.path.endswith("/human-input"):
                self.wfile.write(json.dumps([state["run"]] if self.path.endswith("/runs") else state["run"]).encode())

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield WorkflowRunClient(f"http://127.0.0.1:{server.server_port}", "tenant-a"), state
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


@pytest.mark.asyncio
async def test_inspection_preserves_journal_and_uses_tenant_route(host):
    client, state = host
    assert await client.list_async() == [state["run"]]
    assert await client.read_async("run-1") == state["run"]
    assert state["requests"] == [("GET", "/api/tenants/tenant-a/runs", None), ("GET", "/api/tenants/tenant-a/runs/run-1", None)]


@pytest.mark.asyncio
@pytest.mark.parametrize("command", ["resume", "cancel"])
async def test_commands_preserve_expected_revision_and_never_retry_conflicts(host, command):
    client, state = host
    await client.command_async("run-1", command, 7)
    with pytest.raises(HTTPError) as error:
        await client.command_async("run-1", command, 6)
    assert error.value.code == 409
    assert state["requests"] == [("POST", f"/api/tenants/tenant-a/runs/run-1/{command}", {"expectedRevision": r}) for r in (7, 6)]


@pytest.mark.asyncio
async def test_reconciliation_and_human_answers_send_observations_to_host(host):
    client, state = host
    await client.command_async("run-1", "reconcile", 7, invocation_id="main/loop/2/task", confirmed_stopped_reason="Process exited without receipt")
    await client.answer_async("run-1", 7, "main/input", {"approved": False})
    assert state["requests"][0][2] == {"expectedRevision": 7, "invocationId": "main/loop/2/task", "confirmedStoppedReason": "Process exited without receipt"}
    assert state["requests"][1][1].endswith("/human-input")
    assert state["requests"][1][2] == {"expectedRevision": 7, "invocationId": "main/input", "response": {"approved": False}}


@pytest.mark.asyncio
@pytest.mark.parametrize("field,value", [("schemaVersion", 8), ("tenantId", "tenant-b"), ("runId", "other"), ("revision", True)])
async def test_invalid_or_incompatible_host_records_fail_without_writing(host, field, value):
    client, state = host
    state["run"][field] = value
    with pytest.raises(ValueError):
        await client.read_async("run-1")
    assert len(state["requests"]) == 1
    assert state["requests"][0][0] == "GET"


@pytest.mark.asyncio
@pytest.mark.parametrize("status", [302, 500])
async def test_transport_does_not_follow_redirects_or_retry_server_errors(host, status):
    client, state = host
    state["status"] = status
    with pytest.raises(HTTPError) as error:
        await client.command_async("run-1", "resume", 7)
    assert error.value.code == status
    assert len(state["requests"]) == 1


@pytest.mark.asyncio
async def test_invalid_commands_fail_before_dispatch(host):
    client, state = host
    for command, revision in [("restart", 7), ("resume", -1), ("resume", True), ("reconcile", 7)]:
        with pytest.raises(ValueError):
            await client.command_async("run-1", command, revision)
    assert state["requests"] == []
