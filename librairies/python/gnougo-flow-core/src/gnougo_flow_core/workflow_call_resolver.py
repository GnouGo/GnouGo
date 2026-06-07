from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol
from urllib.parse import urlparse

from .compilation import WorkflowCompiler
from .errors import ErrorCodes, WorkflowRuntimeException
from .models import CompiledDocument, CompiledWorkflow, FetchPolicy, WorkflowDocument
from .parsing import WorkflowParser


@dataclass(slots=True)
class WorkflowCallResolutionContext:
    engine: Any
    ref: dict[str, Any]
    kind: str
    call_depth: int
    call_stack: set[str]


@dataclass(slots=True)
class WorkflowCallResolution:
    workflow: CompiledWorkflow
    workflow_name: str
    call_stack_key: str | None = None


class IWorkflowCallResolver(Protocol):
    async def resolve_async(self, context: WorkflowCallResolutionContext) -> WorkflowCallResolution: ...


class DefaultWorkflowCallResolver:
    def __init__(
        self,
        workspace_root: str | Path | None = None,
        allowed_hostnames: list[str] | tuple[str, ...] | set[str] | None = None,
    ) -> None:
        self.workspace_root = Path(workspace_root).resolve() if workspace_root else None
        self.allowed_hostnames = {h.lower() for h in (allowed_hostnames or []) if str(h).strip()}

    async def resolve_async(self, context: WorkflowCallResolutionContext) -> WorkflowCallResolution:
        if context.kind == "local":
            return self._resolve_local(context)
        if context.kind == "url":
            return await self._resolve_url(context)
        if context.kind == "workspace":
            return await self._resolve_workspace(context)
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Unknown workflow.call kind: {context.kind}")

    def _resolve_local(self, context: WorkflowCallResolutionContext) -> WorkflowCallResolution:
        name = context.ref.get("name")
        if not isinstance(name, str) or not name:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Local workflow.call requires 'name'")
        call_stack_key = f"local:{name}"
        if call_stack_key in context.call_stack:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Cycle detected: workflow '{name}' already in call stack",
            )
        compiled_doc: CompiledDocument | None = context.engine.compiled_document
        if not compiled_doc or name not in compiled_doc.workflows:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Local workflow '{name}' not found")
        return WorkflowCallResolution(compiled_doc.workflows[name], name, call_stack_key)

    async def _resolve_url(self, context: WorkflowCallResolutionContext) -> WorkflowCallResolution:
        fetcher = context.engine.workflow_fetcher
        if fetcher is None:
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_NETWORK, "No workflow fetcher configured")
        url = context.ref.get("url")
        if not isinstance(url, str) or not url:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Remote workflow.call requires 'url'")
        integrity = context.ref.get("integrity")
        policy = context.engine.fetch_policy
        self._enforce_fetch_policy(url, integrity, policy)
        try:
            yaml_text = await fetcher.fetch_async(url, integrity)
        except WorkflowRuntimeException:
            raise
        except Exception as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_NETWORK,
                f"Failed to fetch remote workflow: {exc}",
            ) from exc
        self._enforce_max_size(yaml_text, policy)
        return self._compile_document_reference(yaml_text, context.ref, url)

    async def _resolve_workspace(self, context: WorkflowCallResolutionContext) -> WorkflowCallResolution:
        if self.workspace_root is None:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_POLICY,
                "No workspace root configured for workflow.call kind 'workspace'",
            )
        relative_path = context.ref.get("path") or context.ref.get("name")
        if not isinstance(relative_path, str) or not relative_path:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Workspace workflow.call requires 'path'")
        full_path = self._resolve_workspace_path(relative_path)
        if not full_path.is_file():
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_NETWORK, f"Workspace workflow '{relative_path}' not found")
        yaml_text = full_path.read_text(encoding="utf-8")
        self._enforce_max_size(yaml_text, context.engine.fetch_policy)
        source_path = relative_path.replace("\\", "/")
        return self._compile_document_reference(yaml_text, context.ref, f"workspace:{source_path}")

    def _enforce_fetch_policy(self, url: str, integrity: str | None, policy: FetchPolicy | None) -> None:
        parsed = urlparse(url)
        if not parsed.scheme or not parsed.netloc:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Remote workflow.call requires an absolute URL")
        require_https = False if policy is None else policy.require_https
        if require_https and (parsed.scheme or "").lower() != "https":
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_POLICY, "HTTPS required by policy")
        allow = {h.lower() for h in (policy.allowed_hostnames if policy and policy.allowed_hostnames else self.allowed_hostnames)}
        if allow:
            host = (parsed.hostname or "").lower()
            if host not in allow:
                raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_POLICY, f"Host '{host}' not in allow-list")
        if policy is not None and getattr(policy, "require_integrity", False) and not integrity:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_POLICY,
                "Integrity hash required by fetch policy but missing",
            )

    @staticmethod
    def _enforce_max_size(yaml_text: str, policy: FetchPolicy | None) -> None:
        if policy is None or not getattr(policy, "max_size_bytes", 0):
            return
        size = len(yaml_text.encode("utf-8")) if isinstance(yaml_text, str) else len(yaml_text or b"")
        if size > policy.max_size_bytes:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_POLICY,
                f"Remote workflow ({size} bytes) exceeds max_size_bytes ({policy.max_size_bytes})",
            )

    def _resolve_workspace_path(self, relative_path: str) -> Path:
        candidate = Path(relative_path)
        if candidate.is_absolute():
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_POLICY, "Workspace workflow path must be relative")
        resolved = (self.workspace_root / candidate).resolve()  # type: ignore[operator]
        if not self._is_relative_to(resolved, self.workspace_root):
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_POLICY, "Workspace workflow path traversal is not allowed")
        return resolved

    @staticmethod
    def _is_relative_to(path: Path, root: Path | None) -> bool:
        if root is None:
            return False
        try:
            path.relative_to(root)
            return True
        except ValueError:
            return False

    @classmethod
    def _compile_document_reference(cls, yaml_text: str, ref: dict[str, Any], source_description: str) -> WorkflowCallResolution:
        doc = WorkflowParser.parse(yaml_text)
        compiled = WorkflowCompiler().compile(doc)
        workflow = cls._select_workflow(doc, compiled, ref, source_description)
        return WorkflowCallResolution(workflow, workflow.name, f"{source_description}:{workflow.name}")

    @staticmethod
    def _select_workflow(
        doc: WorkflowDocument,
        compiled: CompiledDocument,
        ref: dict[str, Any],
        source_description: str,
    ) -> CompiledWorkflow:
        export = ref.get("export")
        if isinstance(export, str) and export:
            exports = list(doc.exports or [])
            if exports and export not in exports:
                raise WorkflowRuntimeException(
                    ErrorCodes.WORKFLOW_FETCH_POLICY,
                    f"Workflow '{export}' is not exported from {source_description}",
                )
            if export in compiled.workflows:
                return compiled.workflows[export]
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_POLICY,
                f"Workflow '{export}' is not defined in {source_description}",
            )
        if doc.exports and len(doc.exports) == 1 and doc.exports[0] in compiled.workflows:
            return compiled.workflows[doc.exports[0]]
        if compiled.entrypoint and compiled.entrypoint in compiled.workflows:
            return compiled.workflows[compiled.entrypoint]
        if len(compiled.workflows) == 1:
            return next(iter(compiled.workflows.values()))
        raise WorkflowRuntimeException(
            ErrorCodes.INPUT_VALIDATION,
            f"Could not resolve target workflow from {source_description}",
        )


