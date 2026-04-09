from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_steps import STEP_TYPES
from gnougo_flow_core.step_types import STEP_TYPES as DECLARED_STEP_TYPES


def test_all_registered_steps_are_self_described() -> None:
    engine = WorkflowEngine()
    registry = engine.registry

    snippet_map = registry.get_dsl_snippet_map()
    catalogs = registry.get_step_exception_catalogs()

    described_types = set(snippet_map.keys())
    exception_types = {catalog.step_type for catalog in catalogs}

    assert described_types == set(STEP_TYPES)
    assert exception_types == set(STEP_TYPES)


def test_step_description_filtering_uses_allowed_step_types() -> None:
    engine = WorkflowEngine()
    allowed = {"mcp.list", "mcp.call"}

    snippet_map = engine.registry.get_dsl_snippet_map(allowed)
    catalogs = engine.registry.get_step_exception_catalogs(allowed)

    assert set(snippet_map.keys()) == allowed
    assert {catalog.step_type for catalog in catalogs} == allowed


def test_declared_step_types_match_runtime_step_types() -> None:
    assert STEP_TYPES == DECLARED_STEP_TYPES


