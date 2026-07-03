from __future__ import annotations

import copy
from typing import Any

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class AssertNonNullExecutor:
    step_type = "assert.non_null"
    step_description = "Require values to be non-null and expose them unchanged."
    dsl_snippet = """
### assert.non_null - Require values before using them
```yaml
- id: require_identity
  type: assert.non_null
  input:
    owner: "${data.steps.derive_identity.owner}"
    repo: "${data.steps.derive_identity.repo}"
```
Output: exact resolved input object. Fails if any value is null.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "One or more asserted values are null."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "assert.non_null input must be object")
        null_paths: list[str] = []
        self._collect_null_paths(input_obj, "$", null_paths)
        if null_paths:
            joined = ", ".join(f"{path} is null" for path in null_paths)
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "assert.non_null failed: " + joined)
        return copy.deepcopy(input_obj)

    @classmethod
    def _collect_null_paths(cls, value: Any, path: str, null_paths: list[str]) -> None:
        if value is None:
            null_paths.append(path)
            return
        if isinstance(value, dict):
            for key, child in value.items():
                cls._collect_null_paths(child, f"{path}.{key}", null_paths)
            return
        if isinstance(value, list):
            for index, child in enumerate(value):
                cls._collect_null_paths(child, f"{path}[{index}]", null_paths)
