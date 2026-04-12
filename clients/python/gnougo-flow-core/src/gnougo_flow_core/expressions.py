from __future__ import annotations

import base64
import json
import re
from datetime import datetime
from types import SimpleNamespace
from typing import Any, Callable

from .errors import ErrorCodes, ExpressionParseException, WorkflowRuntimeException


class AttrDict(dict):
    def __getattribute__(self, item: str) -> Any:
        if item in ("__class__", "__dict__", "__module__", "__weakref__"):
            return super().__getattribute__(item)
        if item in self:
            return self[item]
        return super().__getattribute__(item)

    def __getattr__(self, item: str) -> Any:
        return self.get(item)


class AttrList(list):
    def __getattr__(self, item: str) -> Any:
        # Compatibility with LLM-generated expressions like `response.content`
        # when MCP tool outputs are arrays at the root.
        if item in {"content", "items"}:
            return self
        if item == "length":
            return len(self)
        raise AttributeError(item)


def _wrap(value: Any) -> Any:
    if isinstance(value, dict):
        return AttrDict({k: _wrap(v) for k, v in value.items()})
    if isinstance(value, list):
        return AttrList(_wrap(v) for v in value)
    return value


def _json_default(value: Any) -> Any:
    if isinstance(value, dict):
        return value
    if hasattr(value, "model_dump"):
        return value.model_dump()
    return str(value)


def _js_to_py_expr(expression: str) -> str:
    expr = expression
    expr = expr.replace("&&", " and ").replace("||", " or ")
    expr = re.sub(r"(?<![=!])!(?!=)", " not ", expr)
    expr = re.sub(r"\btrue\b", "True", expr)
    expr = re.sub(r"\bfalse\b", "False", expr)
    expr = re.sub(r"\bnull\b", "None", expr)
    expr = expr.replace("?.", ".")
    return expr


class BuiltInFunctions:
    @staticmethod
    def all() -> dict[str, Callable[..., Any]]:
        return {
            "exists": lambda val=None: val is not None,
            "coalesce": lambda *args: next((a for a in args if a is not None), None),
            "len": lambda val=None: len(val) if val is not None else 0,
            "length": lambda val=None: len(val) if val is not None else 0,
            "lower": lambda s="": str(s).lower(),
            "upper": lambda s="": str(s).upper(),
            "trim": lambda s="": str(s).strip(),
            "contains": lambda s, sub: str(sub) in str(s),
            "startsWith": lambda s, p: str(s).startswith(str(p)),
            "endsWith": lambda s, p: str(s).endswith(str(p)),
            "replace": lambda s, old, new: str(s).replace(str(old), str(new)),
            "substring": BuiltInFunctions.substring,
            "toNumber": BuiltInFunctions.to_number,
            "json": lambda v: json.dumps(v, default=_json_default),
            "toJson": lambda v: json.dumps(v, default=_json_default),
            "fromJson": lambda s: json.loads(s) if str(s).strip() else None,
            "now": lambda: datetime.now().isoformat(),
            "formatDate": BuiltInFunctions.format_date,
            "base64": lambda v: base64.b64encode(str(v).encode("utf-8")).decode("utf-8"),
        }

    @staticmethod
    def to_number(value: Any) -> float:
        if value is None:
            return 0.0
        return float(value)

    @staticmethod
    def substring(value: Any, start: Any, length: Any = None) -> str:
        s = str(value)
        st = max(0, int(float(start)))
        if st >= len(s):
            return ""
        if length is None:
            return s[st:]
        ln = max(0, int(float(length)))
        return s[st : st + ln]

    @staticmethod
    def format_date(value: Any, fmt: str = "%Y-%m-%d") -> str:
        try:
            if isinstance(value, (int, float)):
                dt = datetime.fromtimestamp(float(value) / 1000.0)
            else:
                dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
            return dt.strftime(fmt)
        except Exception:
            return str(value)


class ExpressionEvaluator:
    def __init__(self, extra_functions: dict[str, Callable[..., Any]] | None = None) -> None:
        self._functions = BuiltInFunctions.all()
        if extra_functions:
            self._functions.update(extra_functions)

    def evaluate(self, expression: str, context: Any) -> Any:
        expr = _js_to_py_expr(expression)
        wrapped = _wrap(context or {})
        locals_dict: dict[str, Any] = {"data": wrapped}
        if isinstance(wrapped, dict):
            locals_dict.update(wrapped)
        locals_dict.update(self._functions)
        locals_dict["functions"] = SimpleNamespace(**self._functions)
        try:
            return eval(expr, {"__builtins__": {}}, locals_dict)
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.EVAL_ERROR, f"Expression error: {exc}") from exc

    @staticmethod
    def validate(expression: str) -> None:
        expr = _js_to_py_expr(expression)
        try:
            compile(expr, "<expression>", "eval")
        except Exception as exc:
            raise ExpressionParseException(f"Invalid expression: {exc}") from exc

    @staticmethod
    def get_number(value: Any) -> float:
        if value is None:
            return 0.0
        try:
            return float(value)
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.EXPR_TYPE_MISMATCH, f"Expected number but got: {value}") from exc

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
        raise WorkflowRuntimeException(ErrorCodes.EXPR_TYPE_MISMATCH, f"Expected bool but got: {value}")

    @staticmethod
    def get_string(value: Any) -> str:
        if value is None:
            return ""
        if isinstance(value, str):
            return value
        return json.dumps(value, default=_json_default) if isinstance(value, (dict, list)) else str(value)


class StringInterpolator:
    _expr_regex = re.compile(r"\$\{([^}]+)\}")

    def __init__(self, evaluator: ExpressionEvaluator) -> None:
        self._evaluator = evaluator

    @staticmethod
    def has_expressions(value: str | None) -> bool:
        return bool(value and "${" in value)

    def interpolate(self, value: str, context: Any) -> Any:
        trimmed = value.strip()
        if trimmed.startswith("${") and trimmed.endswith("}") and trimmed.count("${") == 1:
            inner = trimmed[2:-1].strip()
            return self._evaluator.evaluate(inner, context)

        def replace(match: re.Match[str]) -> str:
            expr = match.group(1).strip()
            val = self._evaluator.evaluate(expr, context)
            return ExpressionEvaluator.get_string(val)

        return self._expr_regex.sub(replace, value)

    def resolve_deep(self, node: Any, context: Any) -> Any:
        if isinstance(node, str) and self.has_expressions(node):
            return self.interpolate(node, context)
        if isinstance(node, dict):
            return {k: self.resolve_deep(v, context) for k, v in node.items()}
        if isinstance(node, list):
            return [self.resolve_deep(v, context) for v in node]
        return node

