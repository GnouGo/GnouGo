"""Expression engine for gnougo_flow_core.

Backed by the in-tree :mod:`gnougo_flow_core._jsmini` interpreter (no
third-party deps). Mirrors the behavioural surface of the .NET
``ExpressionEvaluator`` (`GnOuGo.Flow.Core/Expressions/ExpressionEvaluator.cs`).
"""
from __future__ import annotations

import base64
import copy
import json
import re
from datetime import datetime, timezone
from types import SimpleNamespace
from typing import Any, Callable

from . import _jsmini
from ._jsmini import (
    ExecutionLimits as JsExecutionLimits,
)
from ._jsmini import (
    Interpreter,
    JsLimitError,
    JsParseError,
    JsRuntimeError,
    Scope,
    parse_expression,
)
from .errors import ErrorCodes, ExpressionParseException, WorkflowRuntimeException


def _json_default(value: Any) -> Any:
    if isinstance(value, dict):
        return value
    if hasattr(value, "model_dump"):
        return value.model_dump()
    return str(value)


# ---------------------------------------------------------------------------
# .NET-style date format token translator (subset)
# ---------------------------------------------------------------------------

_DOTNET_TOKEN_RE = re.compile(r"yyyy|yy|MM|M|dd|d|HH|H|mm|m|ss|s|fff|tt")
_DOTNET_TO_STRFTIME = {
    "yyyy": "%Y",
    "yy": "%y",
    "MM": "%m",
    "M": "%m",
    "dd": "%d",
    "d": "%d",
    "HH": "%H",
    "H": "%H",
    "mm": "%M",
    "m": "%M",
    "ss": "%S",
    "s": "%S",
    "fff": "%f",
    "tt": "%p",
}


def _dotnet_format_to_strftime(fmt: str) -> str:
    if "%" in fmt:
        return fmt
    out: list[str] = []
    i = 0
    while i < len(fmt):
        if fmt[i] == "'":  # literal escape
            j = fmt.find("'", i + 1)
            if j < 0:
                out.append(fmt[i + 1:])
                return "".join(out)
            out.append(fmt[i + 1:j])
            i = j + 1
            continue
        m = _DOTNET_TOKEN_RE.match(fmt, i)
        if m:
            out.append(_DOTNET_TO_STRFTIME[m.group(0)])
            i = m.end()
        else:
            out.append(fmt[i])
            i += 1
    return "".join(out)


def _format_dt(dt: datetime, fmt: str) -> str:
    py_fmt = _dotnet_format_to_strftime(fmt)
    rendered = dt.strftime(py_fmt)
    if "%f" in py_fmt:
        full = dt.strftime("%f")
        rendered = rendered.replace(full, full[:3])
    return rendered


# ---------------------------------------------------------------------------
# Built-in functions
# ---------------------------------------------------------------------------


class BuiltInFunctions:
    @staticmethod
    def all() -> dict[str, Callable[..., Any]]:
        return {
            "exists": lambda val=None: val is not None,
            "coalesce": lambda *args: next((a for a in args if a is not None), None),
            "len": BuiltInFunctions.len_,
            "length": BuiltInFunctions.len_,
            "lower": lambda s="": ExpressionEvaluator.get_string(s).lower(),
            "upper": lambda s="": ExpressionEvaluator.get_string(s).upper(),
            "trim": lambda s="": ExpressionEvaluator.get_string(s).strip(),
            "contains": lambda s, sub: ExpressionEvaluator.get_string(sub) in ExpressionEvaluator.get_string(s),
            "startsWith": lambda s, p: ExpressionEvaluator.get_string(s).startswith(ExpressionEvaluator.get_string(p)),
            "endsWith": lambda s, p: ExpressionEvaluator.get_string(s).endswith(ExpressionEvaluator.get_string(p)),
            "replace": lambda s, old, new: ExpressionEvaluator.get_string(s).replace(
                ExpressionEvaluator.get_string(old), ExpressionEvaluator.get_string(new)
            ),
            "substring": BuiltInFunctions.substring,
            "toNumber": BuiltInFunctions.to_number,
            "string": ExpressionEvaluator.get_string,
            "toString": ExpressionEvaluator.get_string,
            "json": lambda v: json.dumps(v, default=_json_default),
            "toJson": lambda v: json.dumps(v, default=_json_default),
            "pick": BuiltInFunctions.pick,
            "omit": BuiltInFunctions.omit,
            "fromJson": BuiltInFunctions.from_json,
            "now": lambda: datetime.now(timezone.utc).isoformat(),
            "formatDate": BuiltInFunctions.format_date,
            "base64": lambda v: base64.b64encode(ExpressionEvaluator.get_string(v).encode("utf-8")).decode("utf-8"),
        }

    @staticmethod
    def len_(value: Any = None) -> int:
        if value is None:
            return 0
        if isinstance(value, (list, str, dict)):
            return len(value)
        return 0

    @staticmethod
    def to_number(value: Any) -> float:
        if value is None:
            return 0.0
        try:
            return float(value)
        except (TypeError, ValueError):
            return 0.0

    @staticmethod
    def substring(value: Any, start: Any, length: Any = None) -> str:
        s = ExpressionEvaluator.get_string(value)
        st = max(0, int(float(start)))
        if st >= len(s):
            return ""
        if length is None:
            return s[st:]
        ln = max(0, int(float(length)))
        return s[st : st + ln]

    @staticmethod
    def pick(value: Any, *keys: Any) -> dict[str, Any]:
        if not isinstance(value, dict):
            return {}
        selected = BuiltInFunctions._key_set(keys)
        return {k: copy.deepcopy(v) for k, v in value.items() if k in selected}

    @staticmethod
    def omit(value: Any, *keys: Any) -> dict[str, Any]:
        if not isinstance(value, dict):
            return {}
        omitted = BuiltInFunctions._key_set(keys)
        return {k: copy.deepcopy(v) for k, v in value.items() if k not in omitted}

    @staticmethod
    def _key_set(values: tuple[Any, ...]) -> set[str]:
        out: set[str] = set()
        for value in values:
            BuiltInFunctions._collect_keys(value, out)
        return out

    @staticmethod
    def _collect_keys(value: Any, out: set[str]) -> None:
        if value is None:
            return
        if isinstance(value, (list, tuple, set)):
            for item in value:
                BuiltInFunctions._collect_keys(item, out)
            return
        if isinstance(value, dict):
            return
        key = ExpressionEvaluator.get_string(value)
        if key:
            out.add(key)

    @staticmethod
    def from_json(value: Any) -> Any:
        s = ExpressionEvaluator.get_string(value)
        if not s.strip():
            return None
        try:
            return json.loads(s)
        except json.JSONDecodeError:
            return None

    @staticmethod
    def format_date(value: Any, fmt: str = "yyyy-MM-dd") -> str:
        try:
            dt: datetime
            if isinstance(value, bool):
                return ExpressionEvaluator.get_string(value)
            if isinstance(value, (int, float)):
                dt = datetime.fromtimestamp(float(value) / 1000.0, tz=timezone.utc)
            else:
                s = ExpressionEvaluator.get_string(value)
                # Try unix-millis numeric first (mirror .NET FormatDate).
                try:
                    ts = float(s)
                    dt = datetime.fromtimestamp(ts / 1000.0, tz=timezone.utc)
                except ValueError:
                    iso = s.replace("Z", "+00:00")
                    dt = datetime.fromisoformat(iso)
            return _format_dt(dt, fmt)
        except Exception:
            return ExpressionEvaluator.get_string(value)


# ---------------------------------------------------------------------------
# Expression evaluator
# ---------------------------------------------------------------------------

# Top-level keys that should always be visible directly even if they collide
# with a built-in function name. Mirrors .NET behaviour where context keys
# (`inputs`, `steps`, `env`, `error`, `step`) are exposed by name.
_TOP_LEVEL_KEYS_TO_EXPOSE = ("inputs", "steps", "env", "error", "step")


class ExpressionEvaluator:
    def __init__(
        self,
        extra_functions: dict[str, Callable[..., Any]] | None = None,
        limits: Any | None = None,
    ) -> None:
        self._functions: dict[str, Callable[..., Any]] = BuiltInFunctions.all()
        if extra_functions:
            self._functions.update(extra_functions)
        self.limits = self._to_js_limits(limits)

    @staticmethod
    def _to_js_limits(limits: Any | None) -> JsExecutionLimits:
        if isinstance(limits, JsExecutionLimits):
            return limits
        if limits is None:
            return JsExecutionLimits(max_statements=100_000, timeout_seconds=15.0)

        return JsExecutionLimits(
            max_statements=max(1, int(getattr(limits, "max_expression_statements", 100_000) or 100_000)),
            max_nodes=max(1, int(getattr(limits, "max_expression_ast_nodes", 500) or 500)),
            timeout_seconds=max(0.001, float(getattr(limits, "expression_timeout_seconds", 15) or 15)),
            max_call_depth=max(1, int(getattr(limits, "max_function_call_depth", 50) or 50)),
        )

    @property
    def functions(self) -> dict[str, Callable[..., Any]]:
        return self._functions

    def _build_scope(self, context: Any) -> Scope:
        scope = Scope()
        for name, fn in self._functions.items():
            scope.declare(name, fn)
        scope.declare("functions", SimpleNamespace(**self._functions))
        scope.declare("data", context if context is not None else None)
        if isinstance(context, dict):
            for k, v in context.items():
                scope.declare(k, v)
        for key in _TOP_LEVEL_KEYS_TO_EXPOSE:
            if not isinstance(context, dict) or key not in context:
                if not scope.has(key):
                    scope.declare(key, None)
        return scope

    def evaluate(self, expression: str, context: Any) -> Any:
        try:
            node = parse_expression(expression, self.limits)
        except (JsParseError, JsLimitError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.EVAL_ERROR, f"Expression error: {exc}"
            ) from exc
        scope = self._build_scope(context)
        interpreter = Interpreter(self.limits)
        try:
            return interpreter.evaluate_expression(node, scope)
        except (JsRuntimeError, JsLimitError) as exc:
            message = str(exc)
            if isinstance(exc, JsLimitError) and "statement" in message.lower():
                message = (
                    f"Expression exceeded the configured statement limit ({self.limits.max_statements}). "
                    "Increase ExecutionLimits.max_expression_statements or simplify the expression."
                )
            raise WorkflowRuntimeException(
                ErrorCodes.EVAL_ERROR, f"Expression error: {message}"
            ) from exc

    @staticmethod
    def validate(expression: str) -> None:
        try:
            parse_expression(expression)
        except JsParseError as exc:
            raise ExpressionParseException(f"Invalid expression: {exc}") from exc

    @staticmethod
    def get_number(value: Any) -> float:
        if value is None:
            return 0.0
        if isinstance(value, bool):
            return 1.0 if value else 0.0
        try:
            return float(value)
        except (TypeError, ValueError) as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.EXPR_TYPE_MISMATCH, f"Expected number but got: {value}"
            ) from exc

    @staticmethod
    def get_bool(value: Any) -> bool:
        if isinstance(value, bool):
            return value
        if value is None:
            return False
        if isinstance(value, str):
            if value.lower() == "true":
                return True
            if value.lower() == "false":
                return False
        if isinstance(value, (int, float)):
            return bool(value)
        raise WorkflowRuntimeException(
            ErrorCodes.EXPR_TYPE_MISMATCH, f"Expected bool but got: {value}"
        )

    @staticmethod
    def get_string(value: Any) -> str:
        if value is None:
            return ""
        if isinstance(value, str):
            return value
        if isinstance(value, bool):
            return "true" if value else "false"
        if isinstance(value, (int, float)):
            return _jsmini.to_string(value)
        if isinstance(value, (dict, list)):
            return json.dumps(value, default=_json_default)
        return str(value)


# ---------------------------------------------------------------------------
# String interpolation
# ---------------------------------------------------------------------------


def _scan_expressions(value: str) -> list[tuple[int, int, str]]:
    """Return ``(start, end, expression)`` triples for every ``${...}``
    occurrence in *value*, with brace-balanced scanning that respects nested
    string literals. ``end`` is exclusive.
    """
    results: list[tuple[int, int, str]] = []
    n = len(value)
    i = 0
    while i < n:
        if value[i] == "$" and i + 1 < n and value[i + 1] == "{":
            start = i
            i += 2
            depth = 1
            in_str: str | None = None
            j = i
            closed = False
            while j < n:
                c = value[j]
                if in_str is not None:
                    if c == "\\" and j + 1 < n:
                        j += 2
                        continue
                    if c == in_str:
                        in_str = None
                    j += 1
                    continue
                if c in ("'", '"', "`"):
                    in_str = c
                    j += 1
                    continue
                if c == "{":
                    depth += 1
                elif c == "}":
                    depth -= 1
                    if depth == 0:
                        results.append((start, j + 1, value[i:j]))
                        i = j + 1
                        closed = True
                        break
                j += 1
            if not closed:
                # Unterminated ${...}; advance past "${" and continue.
                i = start + 2
            continue
        i += 1
    return results


class StringInterpolator:
    def __init__(self, evaluator: ExpressionEvaluator) -> None:
        self._evaluator = evaluator

    @staticmethod
    def has_expressions(value: str | None) -> bool:
        return bool(value and "${" in value)

    def interpolate(self, value: str, context: Any) -> Any:
        matches = _scan_expressions(value)
        if not matches:
            return value
        trimmed = value.strip()
        if (
            len(matches) == 1
            and trimmed.startswith("${")
            and trimmed.endswith("}")
        ):
            inner = matches[0][2].strip()
            return self._evaluator.evaluate(inner, context)
        out: list[str] = []
        cursor = 0
        for start, end, expr in matches:
            if start > cursor:
                out.append(value[cursor:start])
            val = self._evaluator.evaluate(expr.strip(), context)
            out.append(ExpressionEvaluator.get_string(val))
            cursor = end
        if cursor < len(value):
            out.append(value[cursor:])
        return "".join(out)

    def resolve_deep(self, node: Any, context: Any) -> Any:
        if isinstance(node, str) and self.has_expressions(node):
            return self.interpolate(node, context)
        if isinstance(node, dict):
            return {k: self.resolve_deep(v, context) for k, v in node.items()}
        if isinstance(node, list):
            return [self.resolve_deep(v, context) for v in node]
        return node


