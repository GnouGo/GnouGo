"""Tests for the Mustache templating fixes (Phase 2 — E19/E20/E21)."""

from __future__ import annotations

from gnougo_flow_core.templating import MustacheEngine

# -- E19 ---------------------------------------------------------------------


def test_mustache_comment_is_consumed() -> None:
    assert MustacheEngine.render("a{{!hi}}b", {}) == "ab"
    assert MustacheEngine.render("x{{! some longer comment }}y", {}) == "xy"


# -- E20 ---------------------------------------------------------------------


def test_mustache_html_encodes_named_entities() -> None:
    out = MustacheEngine.render("{{v}}", {"v": "<a href=\"x\">&</a>"})
    assert "&lt;" in out and "&gt;" in out and "&amp;" in out and "&quot;" in out


def test_mustache_html_does_not_encode_single_quote() -> None:
    """`WebUtility.HtmlEncode` does NOT encode single quotes."""
    out = MustacheEngine.render("{{v}}", {"v": "it's"})
    assert out == "it's"


def test_mustache_html_encodes_non_ascii_as_numeric_entity() -> None:
    out = MustacheEngine.render("{{v}}", {"v": "é"})
    assert out == "&#233;"


def test_mustache_triple_does_not_html_encode() -> None:
    out = MustacheEngine.render("{{{v}}}", {"v": "<b>"})
    assert out == "<b>"


# -- E21 ---------------------------------------------------------------------


def test_mustache_invariant_numeric_formatting() -> None:
    assert MustacheEngine.render("{{v}}", {"v": 1}) == "1"
    assert MustacheEngine.render("{{v}}", {"v": 1.5}) == "1.5"
    assert MustacheEngine.render("{{v}}", {"v": 0.1}) == "0.1"
    # whole floats render as integers (mirror double.ToString(InvariantCulture))
    assert MustacheEngine.render("{{v}}", {"v": 1000000.0}) == "1000000"

