from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class SetExecutor:
    step_type = "set"
    step_description = "Set computed variables from resolved input object."
    dsl_snippet = """
### set - Store computed values in step output
```yaml
- id: prepare
  type: set
  input:
    query: "${data.inputs.task}"
    started_at: "${now()}"
```
Output: exact resolved input object.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "The resolved input for `set` must be an object."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "set input must be object")
        # Deep-clone to avoid downstream mutations bleeding back into shared state.
        return copy.deepcopy(input_obj)

