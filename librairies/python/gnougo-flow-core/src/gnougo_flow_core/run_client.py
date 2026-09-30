"""Client for the authoritative schema-9 journal owned by a Flow host."""

from __future__ import annotations

import asyncio
import json
from typing import Any
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # A redirect must not forward credentials or change the command method.
        return None


class WorkflowRunClient:
    """Tenant-scoped HTTP commands; transport errors never trigger automatic retries.

    After an interrupted command, inspect the run to observe its revision and
    recovery status. This client does not store payloads or execute recovery locally.
    """

    def __init__(self, server_url: str, tenant_id: str, *, headers: dict[str, str] | None = None, timeout: float = 30) -> None:
        url = urlsplit(server_url)
        if url.scheme not in {"http", "https"} or not url.netloc or url.query or url.fragment or url.username or url.password:
            raise ValueError("Provide an HTTP(S) server URL without credentials, query or fragment.")
        if not tenant_id.strip() or tenant_id in {".", ".."} or timeout <= 0:
            raise ValueError("A tenant and positive request timeout are required.")
        self.tenant_id = tenant_id
        self._url = server_url.rstrip("/") + "/api/tenants/" + quote(tenant_id, safe="") + "/runs"
        self._headers = dict(headers or {})
        self._timeout = timeout

    async def _request(self, method: str, suffix: str = "", payload: dict[str, Any] | None = None) -> Any:
        def send() -> Any:
            request = Request(self._url + suffix, method=method, headers={**self._headers, "Content-Type": "application/json"},
                              data=None if payload is None else json.dumps(payload, allow_nan=False).encode("utf-8"))
            with build_opener(_NoRedirect()).open(request, timeout=self._timeout) as response:
                body = response.read()
                return json.loads(body) if body else None

        return await asyncio.to_thread(send)

    @staticmethod
    def _path(run_id: str) -> str:
        if not run_id.strip() or run_id in {".", ".."}:
            raise ValueError("A run identity is required.")
        return "/" + quote(run_id, safe="")

    @staticmethod
    def _revision(expected_revision: int) -> dict[str, Any]:
        if type(expected_revision) is not int or expected_revision < 0:
            raise ValueError("A nonnegative expected revision is required.")
        return {"expectedRevision": expected_revision}

    def _validate(self, run: Any, run_id: str | None = None) -> dict[str, Any]:
        if not isinstance(run, dict) or run.get("schemaVersion") != 9:
            raise ValueError("Incompatible execution schema. Regenerate and approve the workflow with a schema-9 host.")
        if run.get("tenantId") != self.tenant_id or not isinstance(run.get("runId"), str) or not run["runId"]:
            raise ValueError("The host returned an unexpected run owner or identity.")
        if run_id is not None and run["runId"] != run_id:
            raise ValueError("The host returned an unexpected run identity.")
        self._revision(run.get("revision"))
        return run

    async def list_async(self) -> list[dict[str, Any]]:
        runs = await self._request("GET")
        if not isinstance(runs, list):
            raise ValueError("The host did not return a run list.")
        return [self._validate(run) for run in runs]

    async def read_async(self, run_id: str) -> dict[str, Any]:
        return self._validate(await self._request("GET", self._path(run_id)), run_id)

    async def command_async(self, run_id: str, command: str, expected_revision: int, *,
                            invocation_id: str | None = None, confirmed_stopped_reason: str | None = None) -> dict[str, Any]:
        if command not in {"resume", "cancel", "reconcile"}:
            raise ValueError("Choose resume, cancel or reconcile.")
        payload = self._revision(expected_revision)
        if command == "reconcile":
            if not invocation_id or not invocation_id.strip():
                raise ValueError("Reconciliation requires an invocation identity.")
            payload["invocationId"] = invocation_id
            if confirmed_stopped_reason is not None:
                if not confirmed_stopped_reason.strip():
                    raise ValueError("The confirmed stopped reason must not be empty.")
                payload["confirmedStoppedReason"] = confirmed_stopped_reason
        elif invocation_id is not None or confirmed_stopped_reason is not None:
            raise ValueError("Reconciliation details require the reconcile command.")
        return self._validate(await self._request("POST", self._path(run_id) + "/" + command, payload), run_id)

    async def answer_async(self, run_id: str, expected_revision: int, invocation_id: str, response: Any) -> None:
        if not invocation_id.strip():
            raise ValueError("A pending invocation identity is required.")
        payload = {**self._revision(expected_revision), "invocationId": invocation_id, "response": response}
        await self._request("POST", self._path(run_id) + "/human-input", payload)
