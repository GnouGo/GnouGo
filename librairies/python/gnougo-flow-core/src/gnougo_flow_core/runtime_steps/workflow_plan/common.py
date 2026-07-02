from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanCommonMixin:
    @staticmethod
    def _coerce_int(value: Any) -> int | None:
        if isinstance(value, bool) or value is None:
            return None
        if isinstance(value, int):
            return value
        if isinstance(value, float):
            return round(value)
        try:
            return int(str(value).strip())
        except Exception:
            return None


    @staticmethod
    def _coerce_float(value: Any) -> float | None:
        if isinstance(value, bool) or value is None:
            return None
        if isinstance(value, (int, float)):
            return float(value)
        try:
            return float(str(value).strip())
        except Exception:
            return None


    @staticmethod
    def _try_get_string(value: Any) -> str | None:
        return value if isinstance(value, str) else None


    @staticmethod
    def _build_unique_key(mapping: dict[str, Any], requested_key: str) -> str:
        if requested_key not in mapping:
            return requested_key
        index = 2
        while f"{requested_key}_{index}" in mapping:
            index += 1
        return f"{requested_key}_{index}"


    @staticmethod
    def _serialize_yaml_value(value: Any) -> str:
        return yaml.dump(value, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()


    @staticmethod
    def _prompt_section(tag_name: str, content: str | None) -> str:
        body = (content or "").rstrip()
        return f"<{tag_name}>\n{body}\n</{tag_name}>"


    @staticmethod
    def _remove_markdown_fence_lines(value: str) -> str:
        if not value or "```" not in value:
            return value
        normalized = value.replace("\r\n", "\n").replace("\r", "\n")
        return "\n".join(line for line in normalized.split("\n") if not line.lstrip().startswith("```"))


    @staticmethod
    def _strip_markdown_code_fence(text: str) -> str:
        candidate = text.strip()
        if not candidate.startswith("```"):
            return candidate

        lines = candidate.splitlines()
        if not lines:
            return candidate

        # Keep only fenced content and ignore optional language marker on first line.
        if lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip().startswith("```"):
            lines = lines[:-1]
        return "\n".join(lines).strip()


    @staticmethod
    def _looks_like_workflow_body(node: Any) -> bool:
        return isinstance(node, dict) and (
            isinstance(node.get("steps"), list)
            or isinstance(node.get("inputs"), dict)
            or isinstance(node.get("outputs"), dict)
            or isinstance(node.get("functions"), str)
        )


    @staticmethod
    def _dump_json(value: Any) -> str:
        if hasattr(value, "model_dump"):
            value = value.model_dump(by_alias=False)
        return json.dumps(value, ensure_ascii=False, default=str, indent=2)


    @classmethod
    def _append_json_block(cls, lines: list[str], indent: str, label: str, value: Any) -> None:
        lines.append(f"{indent}{label}_json: {cls._dump_json(value)}")
