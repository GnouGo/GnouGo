from __future__ import annotations

from .assert_non_null_executor import AssertNonNullExecutor
from .emit_executor import EmitExecutor
from .human_input_executor import HumanInputExecutor
from .llm_call_executor import LlmCallExecutor
from .loop_parallel_executor import LoopParallelExecutor
from .loop_sequential_executor import LoopSequentialExecutor
from .mcp_call_executor import McpCallExecutor
from .mcp_list_executor import McpListExecutor
from .parallel_executor import ParallelExecutor
from .sequence_executor import SequenceExecutor
from .set_executor import SetExecutor
from .switch_executor import SwitchExecutor
from .template_render_executor import TemplateRenderExecutor
from .workflow_call_executor import WorkflowCallExecutor
from .workflow_execute_executor import WorkflowExecuteExecutor
from .workflow_plan_executor import WorkflowPlanExecutor
from .workflow_route_executor import WorkflowRouteExecutor

_EXECUTOR_CLASSES = [
    AssertNonNullExecutor,
    SequenceExecutor,
    ParallelExecutor,
    LoopSequentialExecutor,
    LoopParallelExecutor,
    SwitchExecutor,
    SetExecutor,
    TemplateRenderExecutor,
    LlmCallExecutor,
    WorkflowCallExecutor,
    WorkflowPlanExecutor,
    WorkflowRouteExecutor,
    WorkflowExecuteExecutor,
    McpListExecutor,
    McpCallExecutor,
    EmitExecutor,
    HumanInputExecutor,
]

STEP_TYPES = frozenset(cls.step_type for cls in _EXECUTOR_CLASSES)

__all__ = [
    "AssertNonNullExecutor",
    "SequenceExecutor",
    "ParallelExecutor",
    "LoopSequentialExecutor",
    "LoopParallelExecutor",
    "SwitchExecutor",
    "SetExecutor",
    "TemplateRenderExecutor",
    "LlmCallExecutor",
    "WorkflowCallExecutor",
    "WorkflowPlanExecutor",
    "WorkflowRouteExecutor",
    "WorkflowExecuteExecutor",
    "McpListExecutor",
    "McpCallExecutor",
    "EmitExecutor",
    "HumanInputExecutor",
    "STEP_TYPES",
]
