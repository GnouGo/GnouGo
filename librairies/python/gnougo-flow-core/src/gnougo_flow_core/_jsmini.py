"""Minimal JavaScript-subset interpreter (AST-based).

Implements a hand-written lexer, Pratt-style parser, and tree-walking
evaluator covering the subset of ECMAScript needed by ``gnougo_flow_core``
for both ``${...}`` expressions and the WFScript ``functions:`` block.

Supports:
- literals: numbers (int/float/hex), strings ('...' "..."), true/false/null/undefined
- identifiers and member access (``a.b``, ``a[x]``)
- optional chaining ``?.`` and nullish coalescing ``??``
- unary ``! - + typeof``
- binary ``+ - * / % == != === !== < <= > >= && || ??``
- ternary ``cond ? a : b``
- function calls
- array & object literals
- top-level statements: ``var``/``let``/``const``, ``if/else``, ``return``,
  ``function`` declarations, blocks, expression statements

NO third-party dependencies. Designed to mirror the behaviour of the .NET
reference (`Jint`-based) ``ExpressionEvaluator`` / ``JintSandbox`` insofar
as the existing Python tests and workflows require.
"""
from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Any, Callable, Iterable

# ----------------------------------------------------------------------------
# Errors
# ----------------------------------------------------------------------------


class JsParseError(Exception):
    """Raised when the script cannot be tokenized or parsed."""


class JsRuntimeError(Exception):
    """Raised on evaluation errors (unknown identifier, bad call, ...)."""


class JsLimitError(Exception):
    """Raised when the script exceeds a configured limit
    (statements, nodes, time)."""


# ----------------------------------------------------------------------------
# Limits
# ----------------------------------------------------------------------------


@dataclass(slots=True)
class ExecutionLimits:
    max_statements: int = 10_000
    max_nodes: int = 100_000
    timeout_seconds: float = 5.0
    max_call_depth: int = 200


# ----------------------------------------------------------------------------
# Lexer
# ----------------------------------------------------------------------------

_KEYWORDS = frozenset({
    "var", "let", "const", "if", "else", "return", "function",
    "true", "false", "null", "undefined", "typeof",
})

# Multi-char punctuators (ordered by length, longest first)
_PUNCT = (
    "===", "!==",
    "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "=>",
    "(", ")", "[", "]", "{", "}", ",", ";", ".", "?", ":",
    "=", "<", ">", "+", "-", "*", "/", "%", "!",
)


@dataclass(slots=True)
class Token:
    kind: str  # 'num' | 'str' | 'ident' | 'kw' | 'punct' | 'eof'
    value: Any
    pos: int


def tokenize(src: str) -> list[Token]:
    tokens: list[Token] = []
    i = 0
    n = len(src)
    while i < n:
        c = src[i]
        # whitespace
        if c in " \t\r\n":
            i += 1
            continue
        # line comment
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i + 2)
            i = n if j < 0 else j + 1
            continue
        # block comment
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            if j < 0:
                raise JsParseError("Unterminated block comment")
            i = j + 2
            continue
        # number
        if c.isdigit() or (c == "." and i + 1 < n and src[i + 1].isdigit()):
            j = i
            if c == "0" and i + 1 < n and src[i + 1] in ("x", "X"):
                j = i + 2
                while j < n and src[j] in "0123456789abcdefABCDEF":
                    j += 1
                tokens.append(Token("num", int(src[i:j], 16), i))
                i = j
                continue
            while j < n and src[j].isdigit():
                j += 1
            if j < n and src[j] == ".":
                j += 1
                while j < n and src[j].isdigit():
                    j += 1
            if j < n and src[j] in ("e", "E"):
                j += 1
                if j < n and src[j] in ("+", "-"):
                    j += 1
                while j < n and src[j].isdigit():
                    j += 1
            text = src[i:j]
            value: Any
            if "." in text or "e" in text or "E" in text:
                value = float(text)
            else:
                value = int(text)
            tokens.append(Token("num", value, i))
            i = j
            continue
        # string
        if c == "'" or c == '"':
            quote = c
            j = i + 1
            buf: list[str] = []
            while j < n and src[j] != quote:
                if src[j] == "\\" and j + 1 < n:
                    esc = src[j + 1]
                    if esc == "n":
                        buf.append("\n")
                    elif esc == "t":
                        buf.append("\t")
                    elif esc == "r":
                        buf.append("\r")
                    elif esc == "\\":
                        buf.append("\\")
                    elif esc == "'":
                        buf.append("'")
                    elif esc == '"':
                        buf.append('"')
                    elif esc == "/":
                        buf.append("/")
                    elif esc == "b":
                        buf.append("\b")
                    elif esc == "f":
                        buf.append("\f")
                    elif esc == "0":
                        buf.append("\0")
                    elif esc == "u" and j + 5 < n:
                        buf.append(chr(int(src[j + 2:j + 6], 16)))
                        j += 6
                        continue
                    elif esc == "x" and j + 3 < n:
                        buf.append(chr(int(src[j + 2:j + 4], 16)))
                        j += 4
                        continue
                    else:
                        buf.append(esc)
                    j += 2
                    continue
                buf.append(src[j])
                j += 1
            if j >= n:
                raise JsParseError("Unterminated string literal")
            tokens.append(Token("str", "".join(buf), i))
            i = j + 1
            continue
        # identifier / keyword
        if c.isalpha() or c == "_" or c == "$":
            j = i + 1
            while j < n and (src[j].isalnum() or src[j] in "_$"):
                j += 1
            word = src[i:j]
            if word in _KEYWORDS:
                tokens.append(Token("kw", word, i))
            else:
                tokens.append(Token("ident", word, i))
            i = j
            continue
        # punctuator
        matched = False
        for sym in _PUNCT:
            if src.startswith(sym, i):
                tokens.append(Token("punct", sym, i))
                i += len(sym)
                matched = True
                break
        if matched:
            continue
        raise JsParseError(f"Unexpected character {c!r} at {i}")
    tokens.append(Token("eof", None, n))
    return tokens


# ----------------------------------------------------------------------------
# AST
# ----------------------------------------------------------------------------


@dataclass(slots=True)
class Literal:
    value: Any


@dataclass(slots=True)
class Identifier:
    name: str


@dataclass(slots=True)
class Member:
    obj: Any
    prop: Any  # str when computed=False, AST node when computed=True
    computed: bool = False
    optional: bool = False


@dataclass(slots=True)
class Call:
    callee: Any
    args: list
    optional: bool = False


@dataclass(slots=True)
class Unary:
    op: str
    arg: Any


@dataclass(slots=True)
class Binary:
    op: str
    left: Any
    right: Any


@dataclass(slots=True)
class Logical:
    op: str  # '&&' '||' '??'
    left: Any
    right: Any


@dataclass(slots=True)
class Conditional:
    test: Any
    cons: Any
    alt: Any


@dataclass(slots=True)
class ArrayLit:
    items: list


@dataclass(slots=True)
class ObjectLit:
    pairs: list  # list of (key:str, value_node)


@dataclass(slots=True)
class Assign:
    target: Any  # Identifier or Member
    value: Any


# Statements
@dataclass(slots=True)
class VarDecl:
    kind: str  # 'var' | 'let' | 'const'
    decls: list  # list of (name:str, init:node|None)


@dataclass(slots=True)
class ExprStmt:
    expr: Any


@dataclass(slots=True)
class IfStmt:
    test: Any
    cons: Any  # Block or stmt
    alt: Any  # Block, stmt, or None


@dataclass(slots=True)
class Block:
    stmts: list


@dataclass(slots=True)
class ReturnStmt:
    value: Any  # node or None


@dataclass(slots=True)
class FunctionDecl:
    name: str
    params: list  # list[str]
    body: Block


@dataclass(slots=True)
class Program:
    stmts: list


# ----------------------------------------------------------------------------
# Parser (Pratt)
# ----------------------------------------------------------------------------


class _Parser:
    def __init__(self, tokens: list[Token], limits: ExecutionLimits):
        self.tokens = tokens
        self.pos = 0
        self.limits = limits
        self.nodes_created = 0

    # --- token helpers ---
    def peek(self, offset: int = 0) -> Token:
        return self.tokens[self.pos + offset]

    def eat(self) -> Token:
        t = self.tokens[self.pos]
        self.pos += 1
        return t

    def check(self, kind: str, value: Any = None) -> bool:
        t = self.tokens[self.pos]
        if t.kind != kind:
            return False
        return value is None or t.value == value

    def match(self, kind: str, value: Any = None) -> bool:
        if self.check(kind, value):
            self.pos += 1
            return True
        return False

    def expect(self, kind: str, value: Any = None) -> Token:
        if not self.check(kind, value):
            t = self.tokens[self.pos]
            want = f"{kind} {value!r}" if value is not None else kind
            raise JsParseError(
                f"Expected {want} but got {t.kind} {t.value!r} at {t.pos}"
            )
        return self.eat()

    def _bump_node(self) -> None:
        self.nodes_created += 1
        if self.nodes_created > self.limits.max_nodes:
            raise JsLimitError(
                f"Max AST node count exceeded ({self.limits.max_nodes})"
            )

    # --- entry points ---
    def parse_program(self) -> Program:
        stmts: list[Any] = []
        while not self.check("eof"):
            stmts.append(self.parse_statement())
        self._bump_node()
        return Program(stmts)

    def parse_expression_only(self) -> Any:
        node = self.parse_expression()
        if not self.check("eof"):
            t = self.tokens[self.pos]
            raise JsParseError(
                f"Unexpected token after expression: {t.kind} {t.value!r}"
            )
        return node

    # --- statements ---
    def parse_statement(self) -> Any:
        t = self.peek()
        if t.kind == "kw":
            if t.value in ("var", "let", "const"):
                return self.parse_var_decl()
            if t.value == "if":
                return self.parse_if()
            if t.value == "return":
                return self.parse_return()
            if t.value == "function":
                return self.parse_function_decl()
        if self.check("punct", "{"):
            return self.parse_block()
        if self.check("punct", ";"):
            self.eat()
            return ExprStmt(Literal(None))
        # expression statement
        expr = self.parse_expression()
        self.match("punct", ";")
        self._bump_node()
        return ExprStmt(expr)

    def parse_block(self) -> Block:
        self.expect("punct", "{")
        stmts: list[Any] = []
        while not self.check("punct", "}") and not self.check("eof"):
            stmts.append(self.parse_statement())
        self.expect("punct", "}")
        self._bump_node()
        return Block(stmts)

    def parse_var_decl(self) -> VarDecl:
        kind = self.eat().value  # var/let/const
        decls: list[tuple[str, Any]] = []
        while True:
            name_tok = self.expect("ident")
            init = None
            if self.match("punct", "="):
                init = self.parse_assignment()
            decls.append((name_tok.value, init))
            if not self.match("punct", ","):
                break
        self.match("punct", ";")
        self._bump_node()
        return VarDecl(kind, decls)

    def parse_if(self) -> IfStmt:
        self.expect("kw", "if")
        self.expect("punct", "(")
        test = self.parse_expression()
        self.expect("punct", ")")
        cons = self.parse_statement()
        alt: Any = None
        if self.match("kw", "else"):
            alt = self.parse_statement()
        self._bump_node()
        return IfStmt(test, cons, alt)

    def parse_return(self) -> ReturnStmt:
        self.expect("kw", "return")
        value: Any = None
        if not self.check("punct", ";") and not self.check("punct", "}") and not self.check("eof"):
            value = self.parse_expression()
        self.match("punct", ";")
        self._bump_node()
        return ReturnStmt(value)

    def parse_function_decl(self) -> FunctionDecl:
        self.expect("kw", "function")
        name = self.expect("ident").value
        params = self._parse_params()
        body = self.parse_block()
        self._bump_node()
        return FunctionDecl(name, params, body)

    def _parse_params(self) -> list[str]:
        self.expect("punct", "(")
        params: list[str] = []
        if not self.check("punct", ")"):
            while True:
                params.append(self.expect("ident").value)
                if not self.match("punct", ","):
                    break
        self.expect("punct", ")")
        return params

    # --- expressions ---
    def parse_expression(self) -> Any:
        return self.parse_assignment()

    def parse_assignment(self) -> Any:
        left = self.parse_ternary()
        if self.match("punct", "="):
            right = self.parse_assignment()
            self._bump_node()
            return Assign(left, right)
        return left

    def parse_ternary(self) -> Any:
        cond = self.parse_nullish()
        if self.match("punct", "?"):
            cons = self.parse_assignment()
            self.expect("punct", ":")
            alt = self.parse_assignment()
            self._bump_node()
            return Conditional(cond, cons, alt)
        return cond

    def parse_nullish(self) -> Any:
        left = self.parse_logical_or()
        while self.match("punct", "??"):
            right = self.parse_logical_or()
            self._bump_node()
            left = Logical("??", left, right)
        return left

    def parse_logical_or(self) -> Any:
        left = self.parse_logical_and()
        while self.match("punct", "||"):
            right = self.parse_logical_and()
            self._bump_node()
            left = Logical("||", left, right)
        return left

    def parse_logical_and(self) -> Any:
        left = self.parse_equality()
        while self.match("punct", "&&"):
            right = self.parse_equality()
            self._bump_node()
            left = Logical("&&", left, right)
        return left

    def parse_equality(self) -> Any:
        left = self.parse_comparison()
        while True:
            for op in ("===", "!==", "==", "!="):
                if self.check("punct", op):
                    self.eat()
                    right = self.parse_comparison()
                    self._bump_node()
                    left = Binary(op, left, right)
                    break
            else:
                return left

    def parse_comparison(self) -> Any:
        left = self.parse_additive()
        while True:
            for op in ("<=", ">=", "<", ">"):
                if self.check("punct", op):
                    self.eat()
                    right = self.parse_additive()
                    self._bump_node()
                    left = Binary(op, left, right)
                    break
            else:
                return left

    def parse_additive(self) -> Any:
        left = self.parse_multiplicative()
        while True:
            for op in ("+", "-"):
                if self.check("punct", op):
                    self.eat()
                    right = self.parse_multiplicative()
                    self._bump_node()
                    left = Binary(op, left, right)
                    break
            else:
                return left

    def parse_multiplicative(self) -> Any:
        left = self.parse_unary()
        while True:
            for op in ("*", "/", "%"):
                if self.check("punct", op):
                    self.eat()
                    right = self.parse_unary()
                    self._bump_node()
                    left = Binary(op, left, right)
                    break
            else:
                return left

    def parse_unary(self) -> Any:
        if self.check("punct", "!") or self.check("punct", "-") or self.check("punct", "+"):
            op = self.eat().value
            arg = self.parse_unary()
            self._bump_node()
            return Unary(op, arg)
        if self.check("kw", "typeof"):
            self.eat()
            arg = self.parse_unary()
            self._bump_node()
            return Unary("typeof", arg)
        return self.parse_postfix()

    def parse_postfix(self) -> Any:
        node = self.parse_primary()
        while True:
            if self.match("punct", "."):
                name = self.expect("ident").value
                self._bump_node()
                node = Member(node, name, computed=False, optional=False)
            elif self.match("punct", "?."):
                if self.check("punct", "("):
                    self.eat()
                    args = self._parse_arg_list()
                    self._bump_node()
                    node = Call(node, args, optional=True)
                elif self.match("punct", "["):
                    idx = self.parse_expression()
                    self.expect("punct", "]")
                    self._bump_node()
                    node = Member(node, idx, computed=True, optional=True)
                else:
                    name = self.expect("ident").value
                    self._bump_node()
                    node = Member(node, name, computed=False, optional=True)
            elif self.match("punct", "["):
                idx = self.parse_expression()
                self.expect("punct", "]")
                self._bump_node()
                node = Member(node, idx, computed=True, optional=False)
            elif self.match("punct", "("):
                args = self._parse_arg_list()
                self._bump_node()
                node = Call(node, args, optional=False)
            else:
                return node

    def _parse_arg_list(self) -> list:
        args: list[Any] = []
        if not self.check("punct", ")"):
            while True:
                args.append(self.parse_assignment())
                if not self.match("punct", ","):
                    break
        self.expect("punct", ")")
        return args

    def parse_primary(self) -> Any:
        t = self.peek()
        if t.kind == "num" or t.kind == "str":
            self.eat()
            self._bump_node()
            return Literal(t.value)
        if t.kind == "kw":
            if t.value == "true":
                self.eat()
                self._bump_node()
                return Literal(True)
            if t.value == "false":
                self.eat()
                self._bump_node()
                return Literal(False)
            if t.value == "null":
                self.eat()
                self._bump_node()
                return Literal(None)
            if t.value == "undefined":
                self.eat()
                self._bump_node()
                return Literal(_UNDEFINED)
            if t.value == "function":
                # function expression (no name required)
                self.eat()
                name = ""
                if self.check("ident"):
                    name = self.eat().value
                params = self._parse_params()
                body = self.parse_block()
                self._bump_node()
                return FunctionDecl(name, params, body)
        if t.kind == "ident":
            self.eat()
            self._bump_node()
            return Identifier(t.value)
        if t.kind == "punct":
            if t.value == "(":
                self.eat()
                node = self.parse_expression()
                self.expect("punct", ")")
                return node
            if t.value == "[":
                self.eat()
                items: list[Any] = []
                if not self.check("punct", "]"):
                    while True:
                        items.append(self.parse_assignment())
                        if not self.match("punct", ","):
                            break
                self.expect("punct", "]")
                self._bump_node()
                return ArrayLit(items)
            if t.value == "{":
                self.eat()
                pairs: list[tuple[str, Any]] = []
                if not self.check("punct", "}"):
                    while True:
                        key_tok = self.peek()
                        if key_tok.kind in ("ident", "kw"):
                            key = self.eat().value
                        elif key_tok.kind == "str":
                            key = self.eat().value
                        elif key_tok.kind == "num":
                            key = str(self.eat().value)
                        else:
                            raise JsParseError(
                                f"Expected object key at {key_tok.pos}"
                            )
                        # shorthand {a} not supported -> require ':'
                        self.expect("punct", ":")
                        value = self.parse_assignment()
                        pairs.append((key, value))
                        if not self.match("punct", ","):
                            break
                self.expect("punct", "}")
                self._bump_node()
                return ObjectLit(pairs)
        raise JsParseError(f"Unexpected token {t.kind} {t.value!r} at {t.pos}")


def parse_program(src: str, limits: ExecutionLimits | None = None) -> Program:
    limits = limits or ExecutionLimits()
    tokens = tokenize(src)
    return _Parser(tokens, limits).parse_program()


def parse_expression(src: str, limits: ExecutionLimits | None = None) -> Any:
    limits = limits or ExecutionLimits()
    tokens = tokenize(src)
    return _Parser(tokens, limits).parse_expression_only()


# ----------------------------------------------------------------------------
# Runtime values & scope
# ----------------------------------------------------------------------------


class _ReturnSignal(Exception):
    def __init__(self, value: Any) -> None:
        self.value = value


class Scope:
    __slots__ = ("vars", "parent")

    def __init__(self, parent: "Scope | None" = None) -> None:
        self.vars: dict[str, Any] = {}
        self.parent = parent

    def declare(self, name: str, value: Any) -> None:
        self.vars[name] = value

    def assign(self, name: str, value: Any) -> None:
        s: Scope | None = self
        while s is not None:
            if name in s.vars:
                s.vars[name] = value
                return
            s = s.parent
        # implicit global
        self.vars[name] = value

    def lookup(self, name: str) -> Any:
        s: Scope | None = self
        while s is not None:
            if name in s.vars:
                return s.vars[name]
            s = s.parent
        return _UNDEFINED

    def has(self, name: str) -> bool:
        s: Scope | None = self
        while s is not None:
            if name in s.vars:
                return True
            s = s.parent
        return False


class _Undefined:
    _instance = None

    def __new__(cls) -> "_Undefined":
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def __repr__(self) -> str:
        return "undefined"

    def __bool__(self) -> bool:
        return False


_UNDEFINED = _Undefined()


# ----------------------------------------------------------------------------
# Coercions
# ----------------------------------------------------------------------------


def to_number(v: Any) -> float:
    if v is None or isinstance(v, _Undefined):
        return 0.0
    if isinstance(v, bool):
        return 1.0 if v else 0.0
    if isinstance(v, (int, float)):
        return float(v)
    if isinstance(v, str):
        s = v.strip()
        if s == "":
            return 0.0
        try:
            return float(s)
        except ValueError:
            return float("nan")
    if isinstance(v, list):
        if len(v) == 0:
            return 0.0
        if len(v) == 1:
            return to_number(v[0])
        return float("nan")
    return float("nan")


def to_bool(v: Any) -> bool:
    if v is None or isinstance(v, _Undefined):
        return False
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return v != 0 and not (isinstance(v, float) and math.isnan(v))
    if isinstance(v, str):
        return v != ""
    return True  # objects/arrays are truthy in JS (empty array too!)


def to_string(v: Any) -> str:
    if v is None or isinstance(v, _Undefined):
        return "null" if v is None else "undefined"
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, float):
        if math.isnan(v):
            return "NaN"
        if math.isinf(v):
            return "Infinity" if v > 0 else "-Infinity"
        if v == int(v) and abs(v) < 1e16:
            return str(int(v))
        return repr(v)
    if isinstance(v, int):
        return str(v)
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        return ",".join(to_string(item) for item in v)
    return str(v)


def strict_eq(a: Any, b: Any) -> bool:
    # Treat None and _Undefined as distinct
    if isinstance(a, _Undefined) and isinstance(b, _Undefined):
        return True
    if isinstance(a, _Undefined) or isinstance(b, _Undefined):
        return False
    if a is None and b is None:
        return True
    if a is None or b is None:
        return False
    if isinstance(a, bool) != isinstance(b, bool):
        return False
    # numeric vs string distinct
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
        return float(a) == float(b)
    if type(a) is not type(b):
        return False
    return a == b


def loose_eq(a: Any, b: Any) -> bool:
    # null / undefined equal each other
    if (a is None or isinstance(a, _Undefined)) and (b is None or isinstance(b, _Undefined)):
        return True
    if a is None or isinstance(a, _Undefined) or b is None or isinstance(b, _Undefined):
        return False
    if type(a) is type(b):
        return a == b
    # numeric vs string
    if isinstance(a, (int, float)) and isinstance(b, str):
        return float(a) == to_number(b)
    if isinstance(a, str) and isinstance(b, (int, float)):
        return to_number(a) == float(b)
    if isinstance(a, bool) or isinstance(b, bool):
        return to_number(a) == to_number(b)
    return a == b


def js_typeof(v: Any) -> str:
    if isinstance(v, _Undefined):
        return "undefined"
    if v is None:
        return "object"
    if isinstance(v, bool):
        return "boolean"
    if isinstance(v, (int, float)):
        return "number"
    if isinstance(v, str):
        return "string"
    if callable(v):
        return "function"
    return "object"


# ----------------------------------------------------------------------------
# Member access (with list compatibility shim)
# ----------------------------------------------------------------------------


def get_member(obj: Any, key: Any) -> Any:
    if obj is None or isinstance(obj, _Undefined):
        return _UNDEFINED
    if isinstance(key, float) and key == int(key):
        key = int(key)
    if isinstance(obj, dict):
        if isinstance(key, str) and key in obj:
            return obj[key]
        if isinstance(key, int):
            sk = str(key)
            if sk in obj:
                return obj[sk]
        # JS .length on plain objects: undefined; for strings see below
        return _UNDEFINED
    if isinstance(obj, list):
        if isinstance(key, int):
            if 0 <= key < len(obj):
                return obj[key]
            return _UNDEFINED
        if isinstance(key, str):
            if key == "length":
                return len(obj)
            # Compatibility shim for LLM-generated `response.content` when MCP
            # tool outputs are arrays at the root.
            if key in ("content", "items"):
                return obj
            try:
                idx = int(key)
            except ValueError:
                return _UNDEFINED
            if 0 <= idx < len(obj):
                return obj[idx]
            return _UNDEFINED
        return _UNDEFINED
    if isinstance(obj, str):
        if isinstance(key, int):
            if 0 <= key < len(obj):
                return obj[key]
            return _UNDEFINED
        if isinstance(key, str) and key == "length":
            return len(obj)
        return _UNDEFINED
    # Fallback: attribute access (SimpleNamespace and similar)
    if isinstance(key, str):
        try:
            return getattr(obj, key)
        except AttributeError:
            return _UNDEFINED
    return _UNDEFINED


def set_member(obj: Any, key: Any, value: Any) -> None:
    if isinstance(obj, dict):
        obj[key if isinstance(key, str) else str(key)] = value
        return
    if isinstance(obj, list) and isinstance(key, int):
        while len(obj) <= key:
            obj.append(None)
        obj[key] = value
        return
    raise JsRuntimeError(f"Cannot assign to property {key!r} of {type(obj).__name__}")


# ----------------------------------------------------------------------------
# Interpreter
# ----------------------------------------------------------------------------


class Interpreter:
    def __init__(self, limits: ExecutionLimits | None = None) -> None:
        self.limits = limits or ExecutionLimits()
        self._start: float = 0.0
        self._stmt_count: int = 0
        self._call_depth: int = 0

    # --- entry points ---
    def run_program(self, prog: Program, scope: Scope) -> Any:
        self._start = time.monotonic()
        self._stmt_count = 0
        self._call_depth = 0
        last: Any = None
        try:
            for stmt in prog.stmts:
                last = self._exec_stmt(stmt, scope)
        except _ReturnSignal as r:
            return r.value
        return last

    def evaluate_expression(self, expr: Any, scope: Scope) -> Any:
        self._start = time.monotonic()
        self._stmt_count = 0
        self._call_depth = 0
        return self._eval(expr, scope)

    # --- core ---
    def _check_time(self) -> None:
        if time.monotonic() - self._start >= self.limits.timeout_seconds:
            raise JsLimitError(
                f"Execution timed out (>{self.limits.timeout_seconds}s)"
            )

    def _bump_stmt(self) -> None:
        self._stmt_count += 1
        if self._stmt_count > self.limits.max_statements:
            raise JsLimitError(
                f"Max statements exceeded ({self.limits.max_statements})"
            )
        self._check_time()

    def _exec_stmt(self, stmt: Any, scope: Scope) -> Any:
        self._bump_stmt()
        if isinstance(stmt, ExprStmt):
            return self._eval(stmt.expr, scope)
        if isinstance(stmt, VarDecl):
            for name, init in stmt.decls:
                value = self._eval(init, scope) if init is not None else _UNDEFINED
                scope.declare(name, value)
            return None
        if isinstance(stmt, IfStmt):
            if to_bool(self._eval(stmt.test, scope)):
                return self._exec_stmt(stmt.cons, scope)
            if stmt.alt is not None:
                return self._exec_stmt(stmt.alt, scope)
            return None
        if isinstance(stmt, Block):
            inner = Scope(scope)
            last = None
            for s in stmt.stmts:
                last = self._exec_stmt(s, inner)
            return last
        if isinstance(stmt, ReturnStmt):
            value = self._eval(stmt.value, scope) if stmt.value is not None else _UNDEFINED
            raise _ReturnSignal(value)
        if isinstance(stmt, FunctionDecl):
            fn = self._make_function(stmt, scope)
            if stmt.name:
                scope.declare(stmt.name, fn)
            return fn
        raise JsRuntimeError(f"Unknown statement type: {type(stmt).__name__}")

    def _make_function(self, decl: FunctionDecl, closure: Scope) -> Callable[..., Any]:
        params = decl.params
        body = decl.body
        interp = self

        def _fn(*args: Any) -> Any:
            interp._call_depth += 1
            if interp._call_depth > interp.limits.max_call_depth:
                interp._call_depth -= 1
                raise JsLimitError(
                    f"Max call depth exceeded ({interp.limits.max_call_depth})"
                )
            try:
                inner = Scope(closure)
                for i, name in enumerate(params):
                    inner.declare(name, args[i] if i < len(args) else _UNDEFINED)
                try:
                    for s in body.stmts:
                        interp._exec_stmt(s, inner)
                except _ReturnSignal as r:
                    val = r.value
                    return None if isinstance(val, _Undefined) else val
                return None
            finally:
                interp._call_depth -= 1

        _fn.__name__ = decl.name or "<anonymous>"
        return _fn

    def _eval(self, node: Any, scope: Scope) -> Any:
        # Periodic time check (cheap)
        if (self._stmt_count & 0x3FF) == 0:
            self._check_time()
        if isinstance(node, Literal):
            return node.value
        if isinstance(node, Identifier):
            v = scope.lookup(node.name)
            if isinstance(v, _Undefined):
                # JS would throw ReferenceError; we surface as undefined for
                # parity with Jint's lenient reads on JsonObject/data.
                return None
            return v
        if isinstance(node, Member):
            obj = self._eval(node.obj, scope)
            if node.optional and (obj is None or isinstance(obj, _Undefined)):
                return None
            key = node.prop if not node.computed else self._eval(node.prop, scope)
            res = get_member(obj, key)
            return None if isinstance(res, _Undefined) else res
        if isinstance(node, Call):
            # Method call: if callee is Member, bind this implicitly (not used)
            callee_node = node.callee
            if isinstance(callee_node, Member):
                obj = self._eval(callee_node.obj, scope)
                if (callee_node.optional or node.optional) and (
                    obj is None or isinstance(obj, _Undefined)
                ):
                    return None
                key = (
                    callee_node.prop
                    if not callee_node.computed
                    else self._eval(callee_node.prop, scope)
                )
                fn = get_member(obj, key)
                if isinstance(fn, _Undefined) or fn is None:
                    if node.optional or callee_node.optional:
                        return None
                    raise JsRuntimeError(f"{key} is not a function")
            else:
                fn = self._eval(callee_node, scope)
                if (node.optional) and (fn is None or isinstance(fn, _Undefined)):
                    return None
                if fn is None or isinstance(fn, _Undefined):
                    raise JsRuntimeError("callee is not a function")
            if not callable(fn):
                raise JsRuntimeError(f"value is not callable: {fn!r}")
            args = [self._eval(a, scope) for a in node.args]
            self._bump_stmt()
            return fn(*args)
        if isinstance(node, Unary):
            v = self._eval(node.arg, scope)
            if node.op == "!":
                return not to_bool(v)
            if node.op == "-":
                n = to_number(v)
                return -n if not (isinstance(n, float) and math.isnan(n)) else float("nan")
            if node.op == "+":
                return to_number(v)
            if node.op == "typeof":
                return js_typeof(v)
            raise JsRuntimeError(f"Unknown unary op {node.op}")
        if isinstance(node, Binary):
            op = node.op
            left = self._eval(node.left, scope)
            right = self._eval(node.right, scope)
            if op == "+":
                if isinstance(left, str) or isinstance(right, str):
                    return to_string(left) + to_string(right)
                return to_number(left) + to_number(right)
            if op == "-":
                return to_number(left) - to_number(right)
            if op == "*":
                return to_number(left) * to_number(right)
            if op == "/":
                rn = to_number(right)
                if rn == 0:
                    return float("inf") if to_number(left) > 0 else float("-inf") if to_number(left) < 0 else float("nan")
                return to_number(left) / rn
            if op == "%":
                rn = to_number(right)
                if rn == 0:
                    return float("nan")
                return math.fmod(to_number(left), rn)
            if op == "==":
                return loose_eq(left, right)
            if op == "!=":
                return not loose_eq(left, right)
            if op == "===":
                return strict_eq(left, right)
            if op == "!==":
                return not strict_eq(left, right)
            if op in ("<", "<=", ">", ">="):
                if isinstance(left, str) and isinstance(right, str):
                    if op == "<":
                        return left < right
                    if op == "<=":
                        return left <= right
                    if op == ">":
                        return left > right
                    if op == ">=":
                        return left >= right
                ln, rn = to_number(left), to_number(right)
                if math.isnan(ln) or math.isnan(rn):
                    return False
                if op == "<":
                    return ln < rn
                if op == "<=":
                    return ln <= rn
                if op == ">":
                    return ln > rn
                if op == ">=":
                    return ln >= rn
            raise JsRuntimeError(f"Unknown binary op {op}")
        if isinstance(node, Logical):
            left = self._eval(node.left, scope)
            if node.op == "&&":
                return self._eval(node.right, scope) if to_bool(left) else left
            if node.op == "||":
                return left if to_bool(left) else self._eval(node.right, scope)
            if node.op == "??":
                if left is None or isinstance(left, _Undefined):
                    return self._eval(node.right, scope)
                return left
        if isinstance(node, Conditional):
            return self._eval(node.cons, scope) if to_bool(self._eval(node.test, scope)) else self._eval(node.alt, scope)
        if isinstance(node, ArrayLit):
            return [self._eval(item, scope) for item in node.items]
        if isinstance(node, ObjectLit):
            return {k: self._eval(v, scope) for k, v in node.pairs}
        if isinstance(node, Assign):
            val = self._eval(node.value, scope)
            if isinstance(node.target, Identifier):
                scope.assign(node.target.name, val)
                return val
            if isinstance(node.target, Member):
                obj = self._eval(node.target.obj, scope)
                key = node.target.prop if not node.target.computed else self._eval(node.target.prop, scope)
                set_member(obj, key, val)
                return val
            raise JsRuntimeError("invalid assignment target")
        if isinstance(node, FunctionDecl):  # function expression
            return self._make_function(node, scope)
        raise JsRuntimeError(f"Cannot evaluate node {type(node).__name__}")


def collect_function_decls(prog: Program) -> Iterable[FunctionDecl]:
    for stmt in prog.stmts:
        if isinstance(stmt, FunctionDecl) and stmt.name:
            yield stmt


__all__ = [
    "ExecutionLimits",
    "Interpreter",
    "JsParseError",
    "JsRuntimeError",
    "JsLimitError",
    "Scope",
    "_UNDEFINED",
    "parse_expression",
    "parse_program",
    "collect_function_decls",
    "to_bool",
    "to_number",
    "to_string",
]


