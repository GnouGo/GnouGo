from __future__ import annotations

from datetime import datetime, timezone

from .models import WorkflowCheckpoint


class InMemoryWorkflowCheckpointer:
    """Non-durable in-memory checkpointer, mirroring the .NET test helper."""

    def __init__(self) -> None:
        self._store: dict[str, WorkflowCheckpoint] = {}

    async def save_async(self, checkpoint: WorkflowCheckpoint) -> None:
        saved = checkpoint.model_copy(deep=True)
        saved.timestamp = saved.timestamp or datetime.now(timezone.utc).isoformat()
        self._store[checkpoint.run_id] = saved

    async def load_async(self, run_id: str) -> WorkflowCheckpoint | None:
        checkpoint = self._store.get(run_id)
        return checkpoint.model_copy(deep=True) if checkpoint is not None else None

    async def delete_async(self, run_id: str) -> None:
        self._store.pop(run_id, None)

    async def list_async(
        self,
        tenant_id: str | None = None,
        status: str | None = None,
    ) -> list[WorkflowCheckpoint]:
        results = [cp.model_copy(deep=True) for cp in self._store.values()]
        if tenant_id is not None:
            results = [cp for cp in results if cp.tenant_id == tenant_id]
        if status is not None:
            results = [cp for cp in results if cp.status == status]
        return sorted(results, key=lambda cp: cp.timestamp or "", reverse=True)

