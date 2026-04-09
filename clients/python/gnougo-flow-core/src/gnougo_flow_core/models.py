from __future__ import annotations

from enum import Enum
from typing import Any

from pydantic import BaseModel, ConfigDict, Field

from .errors import WorkflowError


class RetryPolicy(BaseModel):
    max: int = 1
    backoff_ms: int = 1000
    backoff_mult: float = 2.0
    jitter_ms: int = 0


class OnErrorCase(BaseModel):
    if_: str | None = Field(default=None, alias="if")
    action: str = "stop"
    set_output: Any = None
    retry: RetryPolicy | None = None


class OnErrorDef(BaseModel):
    cases: list[OnErrorCase] = Field(default_factory=list)


class InputDef(BaseModel):
    type: str = "any"
    required: bool = True
    default: Any = None
    items: "InputDef | None" = None
    properties: dict[str, "InputDef"] | None = None
    additional_properties: "InputDef | None" = None
    required_properties: list[str] | None = None
    description: str | None = None


class OutputDef(BaseModel):
    expr: str = ""
    type: str = "any"
    description: str | None = None
    items: "OutputDef | None" = None
    properties: dict[str, "OutputDef"] | None = None
    additional_properties: "OutputDef | None" = None
    required_properties: list[str] | None = None

    @staticmethod
    def from_expr(expr: str) -> "OutputDef":
        return OutputDef(expr=expr)


class SwitchCaseDef(BaseModel):
    value: str | None = None
    when: str | None = None
    steps: list["StepDef"] = Field(default_factory=list)


class BranchDef(BaseModel):
    steps: list["StepDef"] = Field(default_factory=list)


class StepDef(BaseModel):
    id: str
    type: str
    if_: str | None = Field(default=None, alias="if")
    input: Any = None
    output: str | None = None
    retry: RetryPolicy | None = None
    on_error: OnErrorDef | None = None
    steps: list["StepDef"] | None = None
    branches: list[BranchDef] | None = None
    cases: list[SwitchCaseDef] | None = None
    expr: str | None = None
    default: list["StepDef"] | None = None
    item_var: str | None = None
    index_var: str | None = None


class WorkflowDef(BaseModel):
    inputs: dict[str, InputDef] | None = None
    functions: str | None = None
    steps: list[StepDef] = Field(default_factory=list)
    outputs: dict[str, OutputDef] | None = None


class WorkflowDocument(BaseModel):
    dsl: int = 1
    name: str | None = None
    meta: dict[str, str] | None = None
    functions: str | None = None
    exports: list[str] | None = None
    entrypoint: str | None = None
    workflows: dict[str, WorkflowDef] = Field(default_factory=dict)
    raw_yaml: str | None = None


class CompiledSwitchCase(BaseModel):
    source: SwitchCaseDef
    steps: list["CompiledStep"] = Field(default_factory=list)


class CompiledStep(BaseModel):
    source: StepDef
    steps: list["CompiledStep"] | None = None
    branches: list[list["CompiledStep"]] | None = None
    cases: list[CompiledSwitchCase] | None = None
    default: list["CompiledStep"] | None = None

    @property
    def id(self) -> str:
        return self.source.id

    @property
    def type(self) -> str:
        return self.source.type


class CompiledWorkflow(BaseModel):
    name: str
    source: WorkflowDef
    steps: list[CompiledStep] = Field(default_factory=list)
    outputs: dict[str, OutputDef] | None = None
    document: "CompiledDocument | None" = None


class CompiledDocument(BaseModel):
    source: WorkflowDocument
    workflows: dict[str, CompiledWorkflow] = Field(default_factory=dict)
    entrypoint: str | None = None


class StepStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    SKIPPED = "skipped"


class StepResult(BaseModel):
    step_id: str
    step_type: str
    status: StepStatus
    output: Any = None
    error: WorkflowError | None = None
    duration: float = 0.0


class RunResult(BaseModel):
    success: bool = True
    outputs: Any = None
    step_results: list[StepResult] = Field(default_factory=list)
    error: WorkflowError | None = None


class ExecutionLimits(BaseModel):
    max_total_steps_executed: int = 10_000
    max_call_depth: int = 20
    max_parallel_branches: int = 50
    max_loop_iterations: int = 1000
    max_expression_ast_nodes: int = 500
    max_switch_cases: int = 100
    max_function_call_depth: int = 50
    log_step_content: bool = False
    run_id: str | None = None


class LlmRuntimeDefaults(BaseModel):
    provider: str | None = None
    model: str | None = None


class LLMTool(BaseModel):
    name: str
    description: str | None = None
    input_schema: Any = None


class LLMToolCall(BaseModel):
    id: str = ""
    name: str
    arguments: Any = None


class LLMRequest(BaseModel):
    provider: str | None = None
    model: str
    prompt: str
    temperature: float | None = None
    structured_output_schema: Any = None
    structured_output_strict: bool | None = None
    tools: list[LLMTool] | None = None


class LLMResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    text: str = ""
    json_payload: Any = Field(default=None, alias="json")
    usage: Any = None
    raw: Any = None
    tool_calls: list[LLMToolCall] | None = None


class TemplateResult(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    text: str | None = None
    json_payload: Any = Field(default=None, alias="json")
    meta: Any = None


class FetchPolicy(BaseModel):
    allowed_hostnames: list[str] = Field(default_factory=list)
    require_https: bool = True
    max_size_bytes: int = 1_048_576
    timeout_ms: int = 30_000
    max_redirects: int = 5
    require_integrity: bool = False


class McpServerMetadata(BaseModel):
    name: str
    description: str | None = None


class McpToolInfo(BaseModel):
    name: str
    description: str | None = None
    input_schema: Any = None


class McpResourceInfo(BaseModel):
    uri: str
    name: str
    description: str | None = None
    mime_type: str | None = None


class McpPromptArgument(BaseModel):
    name: str
    description: str | None = None
    required: bool = False


class McpPromptInfo(BaseModel):
    name: str
    description: str | None = None
    arguments: list[McpPromptArgument] | None = None


class McpCallResult(BaseModel):
    is_error: bool = False
    content: Any = None
    usage: dict[str, Any] | None = None
    model: str | None = None


class McpPromptMessage(BaseModel):
    role: str = "user"
    content: str = ""


class McpGetPromptResult(BaseModel):
    description: str | None = None
    messages: list[McpPromptMessage] = Field(default_factory=list)
    usage: dict[str, Any] | None = None
    model: str | None = None


class HumanInputFieldDef(BaseModel):
    name: str
    type: str = "string"
    required: bool = True
    description: str | None = None
    options: list[str] | None = None
    default: str | None = None


class HumanInputRequest(BaseModel):
    run_id: str
    step_id: str
    prompt: str
    context: Any = None
    choices: list[str] | None = None
    fields: list[HumanInputFieldDef] | None = None
    timeout_ms: int = 300_000


InputDef.model_rebuild()
OutputDef.model_rebuild()
StepDef.model_rebuild()
SwitchCaseDef.model_rebuild()
CompiledSwitchCase.model_rebuild()
CompiledStep.model_rebuild()
CompiledWorkflow.model_rebuild()
CompiledDocument.model_rebuild()

