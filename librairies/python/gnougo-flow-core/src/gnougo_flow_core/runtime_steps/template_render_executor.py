from __future__ import annotations

import json

from gnougo_flow_core.runtime import *  # noqa: F401,F403

_VALID_MODES = {"text", "json", "markdown", "html"}


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
    mode: json   # one of: text | json | markdown | html
    strict: false
```
Output: `{ text: ... }` for text/markdown/html modes or `{ json: ... }` for json mode.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "template.render requires template text or has an unknown 'mode'."),
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
        mode = str(input_obj.get("mode", "text")).lower()
        strict = bool(input_obj.get("strict", False))

        if mode not in _VALID_MODES:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"template.render: unknown mode '{mode}'. Allowed: text|json|markdown|html.",
            )

        # 1. Route through pluggable ITemplateEngine if configured
        custom = ctx.engine.template_engine
        if custom is not None:
            try:
                tr = await custom.render_async(template, data, strict, mode)
            except WorkflowRuntimeException:
                raise
            except Exception as exc:
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_RENDER, str(exc)) from exc
            meta = getattr(tr, "meta", None)
            if isinstance(meta, dict):
                out_meta = dict(meta)
            elif meta is None:
                out_meta = {"engine": "custom"}
            else:
                out_meta = {"engine": "custom", "value": meta}
            out: dict[str, Any] = {"meta": out_meta}
            if getattr(tr, "text", None) is not None:
                out["text"] = tr.text
            if getattr(tr, "json", None) is not None or getattr(tr, "json_payload", None) is not None:
                out["json"] = getattr(tr, "json", None) or getattr(tr, "json_payload", None)
            out["meta"].setdefault("mode", mode)
            return out

        # 2. Built-in Mustache engine
        try:
            rendered = MustacheEngine.render(template, data, strict)
        except MustacheRenderException as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_RENDER, str(exc)) from exc

        result: dict[str, Any] = {"meta": {"engine": "mustache", "mode": mode}}
        if mode == "json":
            try:
                result["json"] = json.loads(rendered)
            except Exception as exc:
                raise WorkflowRuntimeException(
                    ErrorCodes.JSON_PARSE,
                    f"Template output is not valid JSON: {exc}",
                ) from exc
        else:
            # text / markdown / html → identical text payload (no post-processing)
            result["text"] = rendered
        return result
