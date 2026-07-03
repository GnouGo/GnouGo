from __future__ import annotations

import asyncio
import copy
import json
import re
import textwrap
from dataclasses import dataclass, field
from typing import Any

import yaml

from gnougo_flow_core.compilation import ValidationError, WorkflowValidator
from gnougo_flow_core.mcp_cache import cache_prompts, cache_resources, cache_tools, get_cached_prompts, get_cached_resources, get_cached_tools
from gnougo_flow_core.models import InputDef, OutputDef, StepDef, WorkflowDef, WorkflowDocument
from gnougo_flow_core.runtime import *  # noqa: F401,F403
from gnougo_flow_core.workflow_plan_contract_normalizer import (
    collect_weak_output_schema_diagnostics,
    is_weak_yaml_output_schema,
)
from gnougo_flow_core.workflow_plan_diagnostics import (
    build_exception_details,
    build_mcp_discovery_coverage_details,
    build_structured_plan_error,
    build_validation_failure_details,
    format_validation_errors,
    infer_plan_error_code,
    to_prompt_json,
)
from gnougo_flow_core.workflow_plan_dry_run_validator import validate_workflow_plan_dry_run
from gnougo_flow_core.workflow_plan_pipeline_quality_analyzer import (
    analyze_external_artifact_readiness,
    build_main_dataflow_quality_details,
    validate_external_artifact_readiness,
)
from gnougo_flow_core.workflow_plan_semantic_validator import (
    McpToolOutputContract,
    WorkflowSemanticValidationException,
    normalize_mcp_call_input_requests,
    validate_workflow_semantics,
)

_MCP_DISCOVERY_MAX_ATTEMPTS = 3
_MCP_DISCOVERY_RETRY_BASE_DELAY_SECONDS = 0.5


@dataclass(slots=True)
class _PipelinePlannedTool:
    server: str
    kind: str
    method: str
    required: bool = False
    purpose: str = ""
    consumes: list[str] = field(default_factory=list)
    produces: list[str] = field(default_factory=list)


@dataclass(slots=True)
class _WorkflowPipelineSubworkflowSpec:
    name: str
    goal: str
    inputs: dict[str, str]
    outputs: dict[str, str]
    extract_reason: str
    content: str
    generation_prompt: str
    description: str = ""
    work_kind: str = ""
    contract_role: str = ""
    concrete_outcome: str = ""
    input_schemas: dict[str, Any] = field(default_factory=dict)
    output_schemas: dict[str, Any] = field(default_factory=dict)
    planned_tools: list[_PipelinePlannedTool] = field(default_factory=list)
    required_capabilities: list[str] = field(default_factory=list)


@dataclass(slots=True)
class _WorkflowPipelineExtraction:
    subworkflows: list[_WorkflowPipelineSubworkflowSpec]
    main_workflow_prompt: str
    validation_errors: list[str]
    root_causes: list[dict[str, Any]] = field(default_factory=list)
    quality_review: dict[str, Any] | None = None


@dataclass(slots=True)
class _GeneratedLeafWorkflow:
    name: str
    workflow_name: str
    document: WorkflowDocument
    yaml_text: str
    workflow_node: dict[str, Any]
    spec: _WorkflowPipelineSubworkflowSpec | None = None


@dataclass(slots=True)
class _GeneratedMainAssembly:
    main_workflow_node: dict[str, Any]
    document_name: str | None = None
    skill_node: dict[str, Any] | None = None


@dataclass(slots=True)
class _WorkflowPlanModeSelection:
    selected_mode: str
    cyclomatic_complexity: int | None = None
    branch_count: int | None = None
    confidence: float | None = None
    reason: str | None = None
    used_fallback: bool = False
    raw_response: str | None = None


class _NoAliasDumper(yaml.SafeDumper):
    def ignore_aliases(self, data):
        return True

__all__ = [name for name in globals() if not name.startswith("__")]
