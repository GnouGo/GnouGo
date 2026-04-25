"""Tests for the Phase 2 ``formatDate`` parity (item D16)."""
from __future__ import annotations

from gnougo_flow_core.expressions import BuiltInFunctions


def test_format_date_iso_string() -> None:
    assert BuiltInFunctions.format_date("2024-05-12") == "2024-05-12"


def test_format_date_iso_datetime_with_z() -> None:
    assert BuiltInFunctions.format_date("2024-05-12T08:30:00Z", "yyyy-MM-dd HH:mm") == "2024-05-12 08:30"


def test_format_date_unix_millis_int() -> None:
    # 2024-05-12T00:00:00Z = 1715472000000
    assert BuiltInFunctions.format_date(1715472000000, "yyyy-MM-dd") == "2024-05-12"


def test_format_date_unix_millis_string() -> None:
    assert BuiltInFunctions.format_date("1715472000000", "yyyy-MM-dd") == "2024-05-12"


def test_format_date_dotnet_format_tokens() -> None:
    assert BuiltInFunctions.format_date("2024-01-02T03:04:05Z", "yyyy-MM-dd HH:mm:ss") == "2024-01-02 03:04:05"


def test_format_date_unparseable_falls_back_to_input() -> None:
    assert BuiltInFunctions.format_date("not-a-date") == "not-a-date"

