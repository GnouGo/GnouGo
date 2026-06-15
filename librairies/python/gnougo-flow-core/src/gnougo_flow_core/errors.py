from __future__ import annotations

from dataclasses import dataclass
from typing import Any


class ErrorCodes:
    EXPR_PARSE = "EXPR_PARSE"
    EXPR_TYPE_MISMATCH = "EXPR_TYPE_MISMATCH"
    EVAL_ERROR = "EVAL_ERROR"
    INPUT_VALIDATION = "INPUT_VALIDATION"
    TEMPLATE_PLAN = "TEMPLATE_PLAN"
    TEMPLATE_POLICY = "TEMPLATE_POLICY"
    TEMPLATE_SYNTAX = "TEMPLATE_SYNTAX"
    TEMPLATE_RENDER = "TEMPLATE_RENDER"
    TEMPLATE_MISSING_VAR = "TEMPLATE_MISSING_VAR"
    JSON_PARSE = "JSON_PARSE"
    LLM_TIMEOUT = "LLM_TIMEOUT"
    LLM_NETWORK = "LLM_NETWORK"
    LLM_SCHEMA = "LLM_SCHEMA"
    WORKFLOW_FETCH_POLICY = "WORKFLOW_FETCH_POLICY"
    WORKFLOW_FETCH_NETWORK = "WORKFLOW_FETCH_NETWORK"
    WORKFLOW_FETCH_INTEGRITY = "WORKFLOW_FETCH_INTEGRITY"
    WORKFLOW_CYCLE_DETECTED = "WORKFLOW_CYCLE_DETECTED"
    LOOP_LIMIT = "LOOP_LIMIT"
    PARALLEL_LIMIT = "PARALLEL_LIMIT"
    SCRIPT_ERROR = "SCRIPT_ERROR"
    STEP_TYPE_UNKNOWN = "STEP_TYPE_UNKNOWN"
    SKILL_REQUIRED = "SKILL_REQUIRED"
    MCP_CONNECTION_ERROR = "MCP_CONNECTION_ERROR"
    MCP_CALL_ERROR = "MCP_CALL_ERROR"
    MCP_LIST_ERROR = "MCP_LIST_ERROR"
    MCP_PROMPT_ERROR = "MCP_PROMPT_ERROR"
    MCP_TIMEOUT = "MCP_TIMEOUT"
    MCP_SERVER_NOT_FOUND = "MCP_SERVER_NOT_FOUND"


@dataclass(slots=True)
class WorkflowError:
    code: str
    type: str
    message: str
    retryable: bool = False
    details: Any = None


class WorkflowRuntimeException(Exception):
    def __init__(self, code: str, message: str, retryable: bool = False, details: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.retryable = retryable
        self.details = details

    def to_workflow_error(self) -> WorkflowError:
        return WorkflowError(
            code=self.code,
            type=self.code,
            message=str(self),
            retryable=self.retryable,
            details=self.details,
        )


class WorkflowParseException(Exception):
    pass


class ExpressionParseException(Exception):
    pass
