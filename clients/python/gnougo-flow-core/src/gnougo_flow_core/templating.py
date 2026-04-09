from __future__ import annotations

import html
from dataclasses import dataclass
from typing import Any


class MustacheParseException(Exception):
    pass


class MustacheRenderException(Exception):
    pass


@dataclass(slots=True)
class TextToken:
    text: str


@dataclass(slots=True)
class VariableToken:
    name: str


@dataclass(slots=True)
class RawVariableToken:
    name: str


@dataclass(slots=True)
class SectionToken:
    name: str
    children: list[Any]


@dataclass(slots=True)
class InvertedSectionToken:
    name: str
    children: list[Any]


MustacheToken = TextToken | VariableToken | RawVariableToken | SectionToken | InvertedSectionToken


class MustacheParser:
    @staticmethod
    def parse(template: str) -> list[MustacheToken]:
        tokens: list[MustacheToken] = []
        MustacheParser._parse_into(template, 0, tokens, None)
        return tokens

    @staticmethod
    def _parse_into(template: str, pos: int, tokens: list[MustacheToken], closing: str | None) -> int:
        while pos < len(template):
            tag_start = template.find("{{", pos)
            if tag_start < 0:
                if pos < len(template):
                    tokens.append(TextToken(template[pos:]))
                pos = len(template)
                break

            if tag_start > pos:
                tokens.append(TextToken(template[pos:tag_start]))

            if tag_start + 2 < len(template) and template[tag_start + 2] == "{":
                triple_end = template.find("}}}", tag_start + 3)
                if triple_end < 0:
                    raise MustacheParseException("Unterminated triple mustache")
                tokens.append(RawVariableToken(template[tag_start + 3 : triple_end].strip()))
                pos = triple_end + 3
                continue

            tag_end = template.find("}}", tag_start + 2)
            if tag_end < 0:
                raise MustacheParseException("Unterminated tag")

            content = template[tag_start + 2 : tag_end].strip()
            pos = tag_end + 2
            if not content:
                raise MustacheParseException("Empty tag")

            first = content[0]
            if first == "#":
                name = content[1:].strip()
                children: list[MustacheToken] = []
                pos = MustacheParser._parse_into(template, pos, children, name)
                tokens.append(SectionToken(name, children))
            elif first == "^":
                name = content[1:].strip()
                children = []
                pos = MustacheParser._parse_into(template, pos, children, name)
                tokens.append(InvertedSectionToken(name, children))
            elif first == "/":
                name = content[1:].strip()
                if closing is not None and closing == name:
                    return pos
                raise MustacheParseException(f"Unexpected closing tag: {name}")
            elif first == "!":
                continue
            else:
                tokens.append(VariableToken(content))

        if closing is not None:
            raise MustacheParseException(f"Unclosed section: {closing}")
        return pos


class MustacheEngine:
    @staticmethod
    def render(template: str, data: Any, strict: bool = False) -> str:
        tokens = MustacheParser.parse(template)
        return MustacheEngine.render_tokens(tokens, data, strict)

    @staticmethod
    def render_tokens(tokens: list[MustacheToken], data: Any, strict: bool = False) -> str:
        chunks: list[str] = []
        MustacheEngine._render_tokens(tokens, data, strict, chunks)
        return "".join(chunks)

    @staticmethod
    def _render_tokens(tokens: list[MustacheToken], data: Any, strict: bool, chunks: list[str]) -> None:
        for token in tokens:
            if isinstance(token, TextToken):
                chunks.append(token.text)
            elif isinstance(token, VariableToken):
                val = MustacheEngine._resolve(token.name, data, strict)
                if val is not None:
                    chunks.append(html.escape(MustacheEngine._to_text(val)))
            elif isinstance(token, RawVariableToken):
                val = MustacheEngine._resolve(token.name, data, strict)
                if val is not None:
                    chunks.append(MustacheEngine._to_text(val))
            elif isinstance(token, SectionToken):
                MustacheEngine._render_section(token.name, token.children, data, strict, chunks)
            elif isinstance(token, InvertedSectionToken):
                val = MustacheEngine._resolve(token.name, data, False)
                if MustacheEngine._is_falsy(val):
                    MustacheEngine._render_tokens(token.children, data, strict, chunks)

    @staticmethod
    def _render_section(name: str, children: list[MustacheToken], data: Any, strict: bool, chunks: list[str]) -> None:
        val = MustacheEngine._resolve(name, data, False)
        if isinstance(val, list):
            for item in val:
                MustacheEngine._render_tokens(children, item, strict, chunks)
            return
        if isinstance(val, dict):
            MustacheEngine._render_tokens(children, val, strict, chunks)
            return
        if not MustacheEngine._is_falsy(val):
            MustacheEngine._render_tokens(children, data, strict, chunks)

    @staticmethod
    def _resolve(path: str, data: Any, strict: bool) -> Any:
        if data is None:
            if strict:
                raise MustacheRenderException(f"Missing variable: {path}")
            return None
        if path == ".":
            return data

        cur = data
        for part in path.split("."):
            if isinstance(cur, dict) and part in cur:
                cur = cur[part]
            else:
                if strict:
                    raise MustacheRenderException(f"Missing variable: {path}")
                return None
        return cur

    @staticmethod
    def _is_falsy(value: Any) -> bool:
        if value is None:
            return True
        if isinstance(value, bool):
            return not value
        if isinstance(value, (int, float)):
            return value == 0
        if isinstance(value, str):
            return value == ""
        if isinstance(value, list):
            return len(value) == 0
        return False

    @staticmethod
    def _to_text(value: Any) -> str:
        if isinstance(value, bool):
            return "true" if value else "false"
        return str(value)

