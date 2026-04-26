
"""Tests for the WFScript sandbox (Phase 2 — F22/F23)."""
from __future__ import annotations

import pytest

from gnougo_flow_core._jsmini import ExecutionLimits
from gnougo_flow_core.errors import WorkflowRuntimeException
from gnougo_flow_core.scripting import ScriptSandbox


def test_multi_statement_function_with_locals_and_if() -> None:
    sandbox = ScriptSandbox()
    fns = sandbox.load_functions(
        """
        function classify(n) {
          var label;
          if (n > 10) {
            label = "big";
          } else {
            label = "small";
          }
          return label;
        }
        """
    )
    assert fns["classify"](42) == "big"
    assert fns["classify"](1) == "small"


def test_function_can_call_builtin_lower() -> None:
    """F23 — built-ins are pre-registered inside the WFScript scope."""
    sandbox = ScriptSandbox()
    fns = sandbox.load_functions(
        """
        function shout(s) {
          return upper(s);
        }
        function quiet(s) {
          return lower(s);
        }
        """
    )
    assert fns["shout"]("Hello") == "HELLO"
    assert fns["quiet"]("WORLD") == "world"


def test_function_can_call_other_user_function() -> None:
    sandbox = ScriptSandbox()
    fns = sandbox.load_functions(
        """
        function double(x) { return x * 2; }
        function quad(x) { return double(double(x)); }
        """
    )
    assert fns["quad"](3) == 12


def test_let_const_locals() -> None:
    sandbox = ScriptSandbox()
    fns = sandbox.load_functions(
        """
        function compute(a, b) {
          let sum = a + b;
          const factor = 10;
          return sum * factor;
        }
        """
    )
    assert fns["compute"](2, 3) == 50


def test_invalid_script_raises_script_error() -> None:
    sandbox = ScriptSandbox()
    with pytest.raises(WorkflowRuntimeException):
        sandbox.load_functions("function broken( {")


def test_call_depth_limit() -> None:
    sandbox = ScriptSandbox(limits=ExecutionLimits(max_call_depth=10))
    fns = sandbox.load_functions(
        """
        function rec(n) { return rec(n + 1); }
        """
    )
    with pytest.raises(WorkflowRuntimeException):
        fns["rec"](0)


def test_execute_returns_last_expression() -> None:
    sandbox = ScriptSandbox()
    assert sandbox.execute("var x = 1 + 2; x") == 3

