from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class SequenceExecutor:
    step_type = "sequence"
    step_description = "Execute nested steps sequentially."
    dsl_snippet = """
### sequence - Execute sub-steps sequentially
```yaml
- id: setup
  type: sequence
  steps:
    - id: step_a
      type: template.render
      input:
        template: "Hello"
        mode: text
    - id: step_b
      type: template.render
      input:
        template: "World"
        mode: text
```
Output: object with each child step output keyed by step id.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "sequence step requires nested 'steps'."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.steps:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "sequence step requires 'steps'")
        result = RunResult(success=True)
        await ctx.engine.execute_steps_async(ctx.step.steps, ctx.data, result, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span, ct=ctx.ct)
        return {sr.step_id: sr.output for sr in result.step_results if sr.status == StepStatus.SUCCEEDED}
