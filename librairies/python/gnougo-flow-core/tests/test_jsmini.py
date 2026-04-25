"""Tests for the new AST-based expression engine (Phase 2 - D14/D15/D17)."""
from __future__ import annotations

import pytest

from gnougo_flow_core._jsmini import ExecutionLimits
from gnougo_flow_core.errors import ExpressionParseException, WorkflowRuntimeException
from gnougo_flow_core.expressions import ExpressionEvaluator, StringInterpolator


def _ev() -> ExpressionEvaluator:
    return ExpressionEvaluator()


def test_optional_chaining_short_circuits() -> None:
    ev = _ev()
    assert ev.evaluate("a?.b?.c", {"a": None}) is None
    assert ev.evaluate("a?.b?.c", {"a": {"b": None}}) is None
    assert ev.evaluate("a?.b?.c", {"a": {"b": {"c": 42}}}) == 42


def test_nullish_coalescing() -> None:
    ev = _ev()
    assert ev.evaluate("null ?? 'x'", {}) == "x"
    assert ev.evaluate("undefined ?? 'x'", {}) == "x"
    # 0 is not nullish: ?? returns 0
    assert ev.evaluate("v ?? 'x'", {"v": 0}) == 0
    assert ev.evaluate("v ?? 'x'", {"v": ""}) == ""


def test_strict_vs_loose_equality() -> None:
    ev = _ev()
    assert ev.evaluate("1 == '1'", {}) is True
    assert ev.evaluate("1 === '1'", {}) is False
    assert ev.evaluate("null == undefined", {}) is True
    assert ev.evaluate("null === undefined", {}) is False


def test_ternary_unary_typeof() -> None:
    ev = _ev()
    assert ev.evaluate("a > 0 ? 'pos' : 'neg'", {"a": 5}) == "pos"
    assert ev.evaluate("!flag", {"flag": False}) is True
    assert ev.evaluate("-n", {"n": 3}) == -3
    assert ev.evaluate("typeof s", {"s": "hi"}) == "string"
    assert ev.evaluate("typeof n", {"n": 1}) == "number"
    assert ev.evaluate("typeof o", {"o": {}}) == "object"


def test_array_object_literals_and_indexing() -> None:
    ev = _ev()
    assert ev.evaluate("[1, 2, 3][1]", {}) == 2
    assert ev.evaluate("{a: 1, b: 2}.b", {}) == 2
    assert ev.evaluate("o['k']", {"o": {"k": "v"}}) == "v"


def test_top_level_keys_shadow_builtin_names() -> None:
    """D15 — `len` is also a builtin function name; the context key wins
    when explicitly shadowing it via top-level keys."""
    ev = _ev()
    # top-level "json" key shadows built-in json() function
    assert ev.evaluate("json", {"json": "stay"}) == "stay"
    # but data.json still works
    assert ev.evaluate("data.json", {"json": "stay"}) == "stay"


def test_validate_rejects_invalid_expression() -> None:
    with pytest.raises(ExpressionParseException):
        ExpressionEvaluator.validate("a +")


def test_node_limit_rejects_huge_expression() -> None:
    ev = ExpressionEvaluator(limits=ExecutionLimits(max_nodes=20))
    huge = "+".join(["1"] * 100)
    with pytest.raises(WorkflowRuntimeException):
        ev.evaluate(huge, {})


def test_string_interpolation_brace_balanced() -> None:
    """D18 — the scanner respects nested braces and quoted strings."""
    ev = _ev()
    interp = StringInterpolator(ev)
    # nested object literal inside ${...}
    assert interp.interpolate("${ {a:1}.a }", {}) == 1
    # multiple expressions
    assert interp.interpolate("a=${a} b=${b}", {"a": 1, "b": 2}) == "a=1 b=2"
    # quoted string with brace inside
    assert interp.interpolate("${ '}' }", {}) == "}"


def test_member_chain_compatibility_for_lists() -> None:
    ev = _ev()
    data = {"steps": {"tool": {"response": [{"text": "hi"}]}}}
    assert ev.evaluate("data.steps.tool.response.content[0].text", data) == "hi"
    assert ev.evaluate("data.steps.tool.response.length", data) == 1

