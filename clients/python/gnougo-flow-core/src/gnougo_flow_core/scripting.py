"""WFScript sandbox.

Mirrors `GnOuGo.Flow.Core.Scripting.JintSandbox` using the in-tree
:mod:`gnougo_flow_core._jsmini` interpreter. Supports multi-statement
function bodies (``var/let/const``, ``if/else``, locals, ``return``) and
exposes built-in functions inside the WFScript scope so that user
functions can call ``lower(x)``, ``len(x)`` etc. (parity with
``JintSandbox.CreateEngine`` lines 127-143).
"""
from __future__ import annotations

from collections.abc import Callable
from types import SimpleNamespace
from typing import Any

from . import _jsmini
from ._jsmini import (
    ExecutionLimits,
    Interpreter,
    JsLimitError,
    JsParseError,
    JsRuntimeError,
    Scope,
    collect_function_decls,
    parse_program,
)
from .errors import ErrorCodes, WorkflowRuntimeException
from .expressions import BuiltInFunctions


class ScriptSandbox:
    """Sandboxed WFScript loader / executor."""

    def __init__(self, limits: ExecutionLimits | None = None) -> None:
        self.limits = limits or ExecutionLimits()

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------
    def _build_scope(self, data_context: Any | None) -> Scope:
        scope = Scope()
        builtins = BuiltInFunctions.all()
        for name, fn in builtins.items():
            scope.declare(name, fn)
        scope.declare("functions", SimpleNamespace(**builtins))
        if data_context is not None:
            scope.declare("data", data_context)
        return scope

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------
    def load_functions(
        self, script: str, data_context: Any | None = None
    ) -> dict[str, Callable[..., Any]]:
        if not script or not script.strip():
            return {}
        try:
            program = parse_program(script, self.limits)
        except (JsParseError, JsLimitError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR, f"Script error: {exc}"
            ) from exc

        scope = self._build_scope(data_context)
        interpreter = Interpreter(self.limits)
        try:
            interpreter.run_program(program, scope)
        except (JsRuntimeError, JsLimitError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR, f"Script error: {exc}"
            ) from exc

        functions: dict[str, Callable[..., Any]] = {}
        for decl in collect_function_decls(program):
            fn = scope.lookup(decl.name)
            if isinstance(fn, _jsmini._Undefined) or not callable(fn):
                continue
            functions[decl.name] = self._wrap(decl.name, fn)
        if not functions:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR,
                "Unsupported WFScript syntax. Use simple function declarations.",
            )
        return functions

    def execute(self, script: str, data_context: Any | None = None) -> Any:
        try:
            program = parse_program(script, self.limits)
        except (JsParseError, JsLimitError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR, f"Script error: {exc}"
            ) from exc
        scope = self._build_scope(data_context)
        interpreter = Interpreter(self.limits)
        try:
            return interpreter.run_program(program, scope)
        except (JsRuntimeError, JsLimitError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.SCRIPT_ERROR, f"Script error: {exc}"
            ) from exc

    @staticmethod
    def _wrap(name: str, fn: Callable[..., Any]) -> Callable[..., Any]:
        def _invoke(*args: Any) -> Any:
            try:
                return fn(*args)
            except WorkflowRuntimeException:
                raise
            except (JsRuntimeError, JsLimitError) as exc:
                raise WorkflowRuntimeException(
                    ErrorCodes.SCRIPT_ERROR, f"Function '{name}' error: {exc}"
                ) from exc
            except Exception as exc:
                raise WorkflowRuntimeException(
                    ErrorCodes.SCRIPT_ERROR, f"Function '{name}' error: {exc}"
                ) from exc

        _invoke.__name__ = name
        return _invoke



