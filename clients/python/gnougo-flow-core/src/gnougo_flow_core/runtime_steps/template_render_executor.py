from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class TemplateRenderExecutor:
    step_type = "template.render"
    step_description = "Render a Mustache template into text or JSON."
    dsl_snippet = """
### template.render - Render a Mustache template
```yaml
- id: render_payload
  type: template.render
  input:
    template: '{"query":"{{inputs.task}}"}'
    data: "${data}"
    mode: json
    strict: false
```
Output: `{ text: ... }` for text mode or `{ json: ... }` for json mode.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "template.render requires template text."),
        (ErrorCodes.TEMPLATE_RENDER, False, "template rendering failed."),
        (ErrorCodes.JSON_PARSE, False, "rendered template is not valid JSON when mode=json."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "template.render input must be object")

        template = input_obj.get("template")
        if not isinstance(template, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "template.render requires 'template'")

        data = input_obj.get("data")
        mode = str(input_obj.get("mode", "text"))
        strict = bool(input_obj.get("strict", False))

        try:
            rendered = MustacheEngine.render(template, data, strict)
        except MustacheRenderException as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_RENDER, str(exc)) from exc

        result = {"meta": {"engine": "mustache"}}
        if mode == "json":
            import json

            try:
                result["json"] = json.loads(rendered)
            except Exception as exc:
                raise WorkflowRuntimeException(ErrorCodes.JSON_PARSE, f"Template output is not valid JSON: {exc}") from exc
        else:
            result["text"] = rendered
        return result
