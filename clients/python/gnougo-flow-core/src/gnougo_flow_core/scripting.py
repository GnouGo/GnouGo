from __future__ import annotations

import re
from collections.abc import Callable
from typing import Any

from .errors import ErrorCodes, WorkflowRuntimeException
from .expressions import ExpressionEvaluator


class ScriptSandbox:
    """Minimal WFScript loader for function declarations with return expressions."""

    _func_pattern = re.compile(
        r"function\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([^)]*)\)\s*\{\s*return\s+(.+?)\s*;\s*\}",
        re.DOTALL,
    )

    def load_functions(self, script: str) -> dict[str, Callable[..., Any]]:
        functions: dict[str, Callable[..., Any]] = {}
        for match in self._func_pattern.finditer(script):
            name = match.group(1)
            args = [arg.strip() for arg in match.group(2).split(",") if arg.strip()]
            body_expr = match.group(3).strip()

            def _make(n: str, params: list[str], expr: str) -> Callable[..., Any]:
                def _fn(*values: Any) -> Any:
                    local_ctx = {k: (values[i] if i < len(values) else None) for i, k in enumerate(params)}
                    evaluator = ExpressionEvaluator()
                    return evaluator.evaluate(expr, local_ctx)

                _fn.__name__ = n
                return _fn

            functions[name] = _make(name, args, body_expr)

        if script.strip() and not functions:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR,
                "Unsupported WFScript syntax. Use simple function declarations with return expressions.",
            )
        return functions

