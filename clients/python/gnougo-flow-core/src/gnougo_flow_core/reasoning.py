"""Reasoning / thinking effort normalization helpers.

Mirrors `GnOuGo.AI.Core.ChatRequestBuilder.NormalizeOpenAiReasoning` and
`NormalizeOllamaThink` (see `src/GnOuGo.AI.Core/ChatRequestBuilder.cs`).

A `LLMRequest.reasoning` value is one of:
    "minimal" | "low" | "medium" | "high" | "max" | "auto" | None

These helpers translate the generic value to provider-specific payload fields.
"""

from __future__ import annotations

__all__ = ["normalize_openai_reasoning", "normalize_ollama_think"]


def normalize_openai_reasoning(value: str | None) -> str | None:
    """Map a generic reasoning level to the OpenAI ``reasoning_effort`` enum.

    Returns one of ``"minimal" | "low" | "medium" | "high"`` or ``None`` when
    the field must be omitted (``"auto"``, unknown value, or ``None``).
    """
    if not value or not isinstance(value, str):
        return None
    v = value.strip().lower()
    if v in ("auto", ""):
        return None
    if v in ("minimal", "min"):
        return "minimal"
    if v == "low":
        return "low"
    if v in ("medium", "med"):
        return "medium"
    if v in ("high", "max", "maximum"):
        return "high"
    return None


def normalize_ollama_think(value: str | None) -> bool | None:
    """Map a generic reasoning level to the Ollama ``think`` boolean.

    Returns ``None`` when the field must be omitted (``"auto"``, unknown value,
    or ``None``).
    """
    if not value or not isinstance(value, str):
        return None
    v = value.strip().lower()
    if v in ("auto", ""):
        return None
    if v in ("none", "off", "false", "0"):
        return False
    if v in (
        "minimal",
        "min",
        "low",
        "medium",
        "med",
        "high",
        "max",
        "maximum",
        "true",
        "1",
    ):
        return True
    return None

