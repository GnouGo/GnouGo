from __future__ import annotations

import asyncio
import copy
from collections.abc import Iterator
from typing import Any

from .errors import ErrorCodes, WorkflowRuntimeException

_STABLE_LLM_ERROR_CODES = {
    ErrorCodes.LLM_TIMEOUT,
    ErrorCodes.LLM_NETWORK,
    ErrorCodes.LLM_PROVIDER,
}


def classify_llm_failure(exception: BaseException) -> WorkflowRuntimeException | None:
    """Translate provider and transport failures into the stable Flow error contract."""
    if isinstance(exception, asyncio.CancelledError):
        return None

    details = copy.deepcopy(exception.details) if isinstance(exception, WorkflowRuntimeException) else None
    for current in _walk_exception_chain(exception):
        if isinstance(current, asyncio.CancelledError):
            return None
        if isinstance(current, WorkflowRuntimeException) and current.code in _STABLE_LLM_ERROR_CODES:
            return current

        if isinstance(current, TimeoutError):
            return WorkflowRuntimeException(
                ErrorCodes.LLM_TIMEOUT,
                str(current),
                retryable=True,
                details=details,
            )

        status = _read_status_code(current)
        if status in {408, 504}:
            return WorkflowRuntimeException(
                ErrorCodes.LLM_TIMEOUT,
                str(current),
                retryable=True,
                details=details,
            )
        if status is None and isinstance(current, (ConnectionError, OSError)):
            return WorkflowRuntimeException(
                ErrorCodes.LLM_NETWORK,
                str(current),
                retryable=True,
                details=details,
            )
        if status is None:
            continue
        if status in {425, 429} or status >= 500:
            return WorkflowRuntimeException(
                ErrorCodes.LLM_NETWORK,
                str(current),
                retryable=True,
                details=details,
            )
        if 400 <= status <= 499:
            return WorkflowRuntimeException(
                ErrorCodes.LLM_PROVIDER,
                str(current),
                retryable=False,
                details=details,
            )

    return None


def _walk_exception_chain(exception: BaseException) -> Iterator[BaseException]:
    current: BaseException | None = exception
    seen: set[int] = set()
    while current is not None and id(current) not in seen:
        seen.add(id(current))
        yield current
        current = current.__cause__ or current.__context__


def _read_status_code(exception: BaseException) -> int | None:
    candidates: list[Any] = [
        getattr(exception, "status_code", None),
        getattr(exception, "status", None),
        getattr(exception, "code", None),
    ]
    response = getattr(exception, "response", None)
    if response is not None:
        candidates.extend(
            (
                getattr(response, "status_code", None),
                getattr(response, "status", None),
            )
        )

    for candidate in candidates:
        if isinstance(candidate, bool):
            continue
        try:
            value = int(candidate)
        except (TypeError, ValueError):
            continue
        if 100 <= value <= 599:
            return value
    return None
