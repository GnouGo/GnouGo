from __future__ import annotations

from .models import WorkflowCheckpoint


class InMemoryWorkflowCheckpointer:
    """Non-durable in-memory checkpointer, mirroring the .NET test helper."""

    def __init__(self) -> None:
        self._store: dict[str, WorkflowCheckpoint] = {}

    async def save_async(self, checkpoint: WorkflowCheckpoint) -> None:
        self._store[checkpoint.run_id] = checkpoint

    async def load_async(self, run_id: str) -> WorkflowCheckpoint | None:
        return self._store.get(run_id)

    async def delete_async(self, run_id: str) -> None:
        self._store.pop(run_id, None)

    async def list_async(
        self,
        tenant_id: str | None = None,
        status: str | None = None,
    ) -> list[WorkflowCheckpoint]:
        results = list(self._store.values())
        if tenant_id is not None:
            results = [cp for cp in results if cp.tenant_id == tenant_id]
        if status is not None:
            results = [cp for cp in results if cp.status == status]
        return sorted(results, key=lambda cp: cp.timestamp or "", reverse=True)

