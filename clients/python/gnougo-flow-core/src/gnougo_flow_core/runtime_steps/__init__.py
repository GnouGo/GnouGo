from __future__ import annotations

from .sequence_executor import SequenceExecutor
from .parallel_executor import ParallelExecutor
from .loop_sequential_executor import LoopSequentialExecutor
from .loop_parallel_executor import LoopParallelExecutor
from .switch_executor import SwitchExecutor
from .set_executor import SetExecutor
from .template_render_executor import TemplateRenderExecutor
from .llm_call_executor import LlmCallExecutor
from .workflow_call_executor import WorkflowCallExecutor
from .workflow_plan_executor import WorkflowPlanExecutor
from .workflow_execute_executor import WorkflowExecuteExecutor
from .mcp_list_executor import McpListExecutor
from .mcp_call_executor import McpCallExecutor
from .emit_executor import EmitExecutor
from .human_input_executor import HumanInputExecutor
 
_EXECUTOR_CLASSES = [
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
    WorkflowExecuteExecutor,
    McpListExecutor,
    McpCallExecutor,
    EmitExecutor,
    HumanInputExecutor,
]

STEP_TYPES = frozenset(cls.step_type for cls in _EXECUTOR_CLASSES)

__all__ = [
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
    "WorkflowExecuteExecutor",
    "McpListExecutor",
    "McpCallExecutor",
    "EmitExecutor",
    "HumanInputExecutor",
    "STEP_TYPES",
]
