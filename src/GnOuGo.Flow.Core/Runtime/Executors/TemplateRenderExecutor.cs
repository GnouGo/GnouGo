using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Templating;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Renders a Mustache template with given data.
/// </summary>
public sealed class TemplateRenderExecutor : IStepExecutor
{
    public string StepType => "template.render";

    public string DslSnippet => """
        ### template.render — Render a Mustache template
        ```yaml
        - id: greet
          type: template.render
          input:
            engine: mustache                    # required
            template: "Hello {{name}}, {{count}} items"  # required — Mustache template string
            data:                               # optional — variables for the template
              name: "${data.inputs.name}"
              count: "${len(data.inputs.items)}"
            mode: text                          # "text" (default) or "json"
        ```
        Output (mode=text): `{ text: "rendered string", meta: { engine: "mustache" } }`
        Output (mode=json): `{ json: { ... }, meta: { engine: "mustache" } }`
        """;

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input object or required `template` field is missing."),
        new(ErrorCodes.TemplateRender, false, "Mustache rendering failed while evaluating the template against the provided data."),
        new(ErrorCodes.JsonParse, false, "`mode: json` was requested but the rendered text is not valid JSON.")
    };

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "template.render input must be object");

        var template = input["template"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "template.render requires 'template'");

        var data = input["data"];
        var mode = input["mode"]?.GetValue<string>() ?? "text";
        var strict = input["strict"]?.GetValue<bool>() ?? false;

        try
        {
            var rendered = MustacheEngine.Render(template, data, strict);
            var result = new JsonObject { ["meta"] = new JsonObject { ["engine"] = "mustache" } };

            if (mode == "json")
            {
                try
                {
                    result["json"] = JsonNode.Parse(rendered);
                }
                catch (JsonException ex)
                {
                    ctx.Engine.Logger.LogError(ex, "template.render output is not valid JSON");
                    throw new WorkflowRuntimeException(ErrorCodes.JsonParse,
                        $"Template output is not valid JSON: {ex.Message}");
                }
            }
            else
            {
                result["text"] = rendered;
            }

            return Task.FromResult<JsonNode?>(result);
        }
        catch (MustacheRenderException ex)
        {
            ctx.Engine.Logger.LogError(ex, "template.render Mustache rendering failed");
            throw new WorkflowRuntimeException(ErrorCodes.TemplateRender, ex.Message);
        }
    }
}
