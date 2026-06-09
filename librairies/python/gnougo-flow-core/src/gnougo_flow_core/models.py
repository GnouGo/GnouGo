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


class WorkflowSkillDef(BaseModel):
    description: str | None = None
    tags: list[str] | None = None
    inputs: dict[str, InputDef] | None = None
    outputs: dict[str, OutputDef] | None = None


class WorkflowDocument(BaseModel):
    # Mirrors .NET WorkflowDocument.Version. `version` is the canonical field.
    model_config = ConfigDict(populate_by_name=True)

    version: int = 1
    name: str | None = None
    meta: dict[str, str] | None = None
    skill: WorkflowSkillDef | None = None
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


class WorkflowCheckpoint(BaseModel):
    run_id: str
    workflow_name: str
    next_step_index: int = 0
    step_outputs: dict[str, Any] = Field(default_factory=dict)
    inputs: Any = None
    workflow_yaml: str = ""
    status: str = "running"
    timestamp: str | None = None
    tenant_id: str | None = None


class ExecutionLimits(BaseModel):
    max_total_steps_executed: int = 10_000
    max_call_depth: int = 20
    max_parallel_branches: int = 50
    max_loop_iterations: int = 1000
    max_expression_ast_nodes: int = 500
    max_expression_statements: int = 100_000
    expression_timeout_seconds: int = 15
    expression_memory_limit_bytes: int = 50_000_000
    max_switch_cases: int = 100
    max_function_call_depth: int = 50
    log_step_content: bool = False
    run_id: str | None = None


class LlmRuntimeDefaults(BaseModel):
    provider: str | None = None
    model: str | None = None


class WorkflowRouteCandidateQuery(BaseModel):
    ref: dict[str, Any]
    kind: str = ""
    tags_any: list[str] = Field(default_factory=list)
    tags_all: list[str] = Field(default_factory=list)
    exclude_tags: list[str] = Field(default_factory=list)
    limit: int | None = None


class WorkflowRouteCandidate(BaseModel):
    id: str = ""
    name: str = ""
    ref: dict[str, Any]
    description: str | None = None
    tags: list[str] = Field(default_factory=list)
    inputs: Any = None
    outputs: Any = None


class ModelPricingMetadata(BaseModel):
    """Pricing metadata for a model, expressed per one million tokens."""

    model_config = ConfigDict(populate_by_name=True)

    currency: str = "USD"
    input_per_1m_tokens: float | None = Field(default=None, alias="inputPer1MTokens")
    output_per_1m_tokens: float | None = Field(default=None, alias="outputPer1MTokens")
    cached_input_per_1m_tokens: float | None = Field(default=None, alias="cachedInputPer1MTokens")
    reasoning_output_per_1m_tokens: float | None = Field(default=None, alias="reasoningOutputPer1MTokens")


class ModelCapabilityMetadata(BaseModel):
    """Capability metadata used to decide which optional request parameters are safe to emit."""

    model_config = ConfigDict(populate_by_name=True)

    supports_temperature: bool | None = Field(default=None, alias="supportsTemperature")
    supports_reasoning_effort: bool | None = Field(default=None, alias="supportsReasoningEffort")
    supports_structured_output: bool | None = Field(default=None, alias="supportsStructuredOutput")
    supports_tools: bool | None = Field(default=None, alias="supportsTools")
    supports_json_mode: bool | None = Field(default=None, alias="supportsJsonMode")
    supports_vision: bool | None = Field(default=None, alias="supportsVision")
    supports_audio: bool | None = Field(default=None, alias="supportsAudio")
    supports_embeddings: bool | None = Field(default=None, alias="supportsEmbeddings")
    supported_reasoning_efforts: list[str] | None = Field(default=None, alias="supportedReasoningEfforts")
    unsupported_request_parameters: list[str] | None = Field(default=None, alias="unsupportedRequestParameters")


class LLMModelMetadata(BaseModel):
    """Complete model metadata: limits, pricing, capabilities and extension values."""

    model_config = ConfigDict(populate_by_name=True)

    id: str = ""
    provider_type: str | None = Field(default=None, alias="providerType")
    display_name: str | None = Field(default=None, alias="displayName")
    owned_by: str | None = Field(default=None, alias="ownedBy")
    context_window_tokens: int | None = Field(default=None, alias="contextWindowTokens")
    max_input_tokens: int | None = Field(default=None, alias="maxInputTokens")
    max_output_tokens: int | None = Field(default=None, alias="maxOutputTokens")
    pricing: ModelPricingMetadata | None = None
    capabilities: ModelCapabilityMetadata = Field(default_factory=ModelCapabilityMetadata)
    aliases: dict[str, str] = Field(default_factory=dict)
    extra: dict[str, Any] = Field(default_factory=dict)


class LLMOptions(BaseModel):
    """Model metadata options for the Python runtime."""

    model_config = ConfigDict(populate_by_name=True)

    model_metadata_files: list[str] = Field(default_factory=list, alias="ModelMetadataFiles")
    model_overrides: dict[str, LLMModelMetadata] = Field(default_factory=dict, alias="ModelOverrides")


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
    # Optional thinking / reasoning effort. Mirrors .NET LLMRequest.Reasoning.
    # Accepted: "minimal" | "low" | "medium" | "high" | "max" | "auto" | None.
    # "auto" / None means "let the provider decide" (no field emitted).
    # "max" maps to the highest provider-supported level (e.g. "high" for OpenAI).
    reasoning: str | None = None
    tools: list[LLMTool] | None = None
    max_tokens: int | None = None
    # When True, hints to the provider that this is a background/planning call
    # (lower priority queue, no interactive latency budget). Mirrors .NET UseBackgroundMode.
    use_background_mode: bool = False


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
    model_config = ConfigDict(populate_by_name=True)

    name: str
    description: str | None = None
    discovery_timeout_seconds: int | None = Field(default=None, alias="DiscoveryTimeoutSeconds")
    call_timeout_seconds: int | None = Field(default=None, alias="CallTimeoutSeconds")


class McpToolInfo(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    name: str
    description: str | None = None
    input_schema: Any = None
    output_schema: Any = Field(default=None, alias="outputSchema")
    example_response: Any = Field(default=None, alias="exampleResponse")


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
