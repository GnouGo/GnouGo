using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const int SplitMinimumWorkflowSteps = 5;

    private static async Task<JsonNode?> ExecuteSplitAsync(StepExecutionContext ctx, JsonObject input, CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, "No LLM client configured");

        var generator = input["generator"] as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "workflow.plan requires 'generator'");

        var requestedModel = generator["model"]?.GetValue<string>();
        var requestedProvider = generator["provider"]?.GetValue<string>();
        var (provider, model) = ctx.Engine.ResolveLlmTarget(requestedProvider, requestedModel);
        model ??= "gpt-4";

        var instruction = generator["instruction"]?.GetValue<string>() ?? "";
        var generatorContext = generator["context"]?.GetValue<string>() ?? "";
        var requestedWorkflowName = generator["workflow_name"]?.GetValue<string>()
            ?? generator["name"]?.GetValue<string>();
        var requestedDescription = generator["description"]?.GetValue<string>();
        var validate = input["validate"] as JsonObject;
        var reasoning = generator["reasoning"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(reasoning))
            reasoning = "medium";

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Planning split workflow manifest with {model}..."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var manifestYaml = await GenerateSplitManifestYamlAsync(
            llmClient,
            provider,
            model,
            reasoning,
            instruction,
            generatorContext,
            ctx,
            ct);

        WorkflowPlanManifest manifest;
        try { manifest = WorkflowPlanManifestParser.Parse(manifestYaml); }
        catch (Exception ex) { throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Split manifest parse failed: {ex.Message}"); }

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, requestedWorkflowName, requestedDescription);
        manifestYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest, includeRuntimePaths: false);
        var manifestMermaid = WorkflowPlanManifestCompiler.CompileMermaid(manifest);

        try { WorkflowPlanManifestValidator.Validate(manifest); }
        catch (Exception ex) { throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Split manifest validation failed: {ex.Message}"); }

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Split manifest algorithm diagram:\n```mermaid\n" + manifestMermaid + "\n```"),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking"),
            new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "split_manifest"),
            new KeyValuePair<string, object?>("gnougo-flow.plan.diagram.format", "mermaid")
        });

        ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Generating {manifest.SubPlans.Count(plan => plan.Algorithm == null)} leaf sub-plan workflow(s) and {manifest.SubPlans.Count(plan => plan.Algorithm != null)} parent sub-plan workflow(s) in dependency order..."),
            new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
        });

        var generationBatches = WorkflowPlanManifestDependencyPlanner.BuildGenerationBatches(manifest);
        var generatedSubPlans = new List<GeneratedSplitSubPlan>();
        var generatedById = new Dictionary<string, GeneratedSplitSubPlan>(StringComparer.Ordinal);
        for (var batchIndex = 0; batchIndex < generationBatches.Count; batchIndex++)
        {
            var batch = generationBatches[batchIndex];
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message", $"Generating split sub-plan batch {batchIndex + 1}/{generationBatches.Count}: {string.Join(", ", batch.Select(static plan => plan.Id))}"),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "thinking")
            });

            var batchTasks = batch
                .Select(subPlan => GenerateSubPlanAsync(ctx, input, generator, instruction, generatorContext, manifest, subPlan, generatedById, ct))
                .ToArray();
            var generatedBatch = await Task.WhenAll(batchTasks);
            foreach (var generated in generatedBatch)
            {
                generatedSubPlans.Add(generated);
                generatedById[generated.Id] = generated;
            }
        }

        string mainYaml;
        try { mainYaml = WorkflowPlanSelfContainedComposer.ComposeMainYaml(manifest, generatedSubPlans); }
        catch (Exception ex) { throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Split main compilation failed: {ex.Message}"); }

        WorkflowDocument mainDoc;
        try
        {
            mainDoc = ParseAndValidateGeneratedWorkflow(mainYaml);
            ValidateGeneratedWorkflowForPlan(mainDoc, discovered: null);
            ValidateSplitWorkflowMinimumStepCount(mainDoc, "main split bundle");
        }
        catch (Exception ex) { throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Split main workflow validation failed: {ex.Message}"); }

        if (validate?["dry_run"]?.GetValue<bool>() ?? false)
        {
            try
            {
                var validationDiscovered = await DiscoverMcpServersAsync(
                    ctx.Engine.McpClientFactory,
                    ctx.Engine.McpCache,
                    ctx.Engine.Logger,
                    ctx,
                    candidateServers: null,
                    ct);
                await WorkflowPlanDryRunValidator.ValidateAsync(
                    mainDoc,
                    BuildDryRunMcpClientFactory(validationDiscovered),
                    ctx.Engine.Logger,
                    ct);
            }
            catch (Exception ex)
            {
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Split main workflow dry_run failed: {ex.Message}");
            }
        }

        var workflows = new JsonObject
        {
            ["./" + manifest.Name + "/workflow.yaml"] = mainYaml
        };
        foreach (var subPlan in generatedSubPlans)
            workflows[subPlan.Path] = subPlan.Yaml;

        return new JsonObject
        {
            ["manifest"] = new JsonObject
            {
                ["yaml"] = manifestYaml,
                ["mermaid"] = manifestMermaid,
                ["name"] = manifest.Name,
                ["subplans"] = new JsonArray(manifest.SubPlans.Select(static plan => (JsonNode)new JsonObject
                {
                    ["id"] = plan.Id,
                    ["path"] = plan.Path,
                    ["responsibility"] = plan.Responsibility,
                    ["composite"] = plan.Algorithm != null
                }).ToArray())
            },
            ["main.yaml"] = mainYaml,
            ["workflows"] = workflows,
            ["diagnostics"] = new JsonArray
            {
                (JsonNode)JsonValue.Create($"Generated split manifest with {manifest.SubPlans.Count} sub-plan(s).")!,
                (JsonNode)JsonValue.Create("Generated split manifest Mermaid diagram:\n" + manifestMermaid)!,
                (JsonNode)JsonValue.Create("Sub-plans were generated in dependency order. Parent and main workflows embed generated children as local workflows for standalone validation and dry-run.")!
            },
            ["meta"] = new JsonObject
            {
                ["mode"] = "split",
                ["model"] = model,
                ["generation_batches"] = new JsonArray(generationBatches.Select(static batch => (JsonNode)new JsonArray(batch.Select(static plan => (JsonNode)JsonValue.Create(plan.Id)!).ToArray())).ToArray())
            }
        };
    }

    private static async Task<string> GenerateSplitManifestYamlAsync(
        ILLMClient llmClient,
        string? provider,
        string model,
        string? reasoning,
        string instruction,
        string? context,
        StepExecutionContext ctx,
        CancellationToken ct)
    {
        var prompt = BuildSplitManifestPrompt(instruction, context);

        using var manifestSpan = ctx.BeginTelemetrySpan("workflow.plan.split_manifest", "generation", new[]
        {
            new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
            new KeyValuePair<string, object?>("gen_ai.system", provider ?? "openai"),
            new KeyValuePair<string, object?>("gen_ai.request.model", model),
            new KeyValuePair<string, object?>("gen_ai.request.background", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.background_requested", true),
            new KeyValuePair<string, object?>("gnougo-flow.plan.mode", "split")
        });

        if (ctx.Limits.LogStepContent)
        {
            var promptAttributes = new[]
            {
                new KeyValuePair<string, object?>("gen_ai.prompt", prompt),
                new KeyValuePair<string, object?>("prompt.role", "user"),
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "split_manifest")
            };
            manifestSpan.AddEvent("gen_ai.content.prompt", promptAttributes);
            ctx.AddTelemetryEvent("gen_ai.content.prompt", promptAttributes);
        }

        try
        {
            var response = await llmClient.CallAsync(new LLMRequest
            {
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Reasoning = reasoning,
                UseBackgroundMode = true
            }, ct);

            manifestSpan.SetAttribute("gen_ai.response.model", model);
            manifestSpan.SetAttribute("gen_ai.response.finish_reason", "stop");
            AddUsageAttributes(manifestSpan, response.Usage, model, provider);
            SetStepUsageTelemetry(ctx, response.Usage, model, provider);
            if (ctx.Limits.LogStepContent && !string.IsNullOrWhiteSpace(response.Text))
            {
                var completionAttributes = new[]
                {
                    new KeyValuePair<string, object?>("gen_ai.completion", response.Text),
                    new KeyValuePair<string, object?>("completion.role", "assistant"),
                    new KeyValuePair<string, object?>("completion.finish_reason", "stop"),
                    new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gnougo-flow.plan.phase", "split_manifest")
                };
                manifestSpan.AddEvent("gen_ai.content.completion", completionAttributes);
                ctx.AddTelemetryEvent("gen_ai.content.completion", completionAttributes);
            }

            var yaml = StripMarkdownFences(response.Text);
            manifestSpan.SetAttribute("gnougo-flow.plan.manifest_length", yaml.Length);
            return yaml;
        }
        catch (Exception ex)
        {
            manifestSpan.Fail(ex);
            throw;
        }
    }

    private static string BuildSplitManifestPrompt(string instruction, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a GnOuGo.Flow split-planning assistant. Return ONLY a YAML manifest, no markdown fences.");
        sb.AppendLine("Do not generate executable workflow YAML. Generate a compact orchestration sketch only.");
        sb.AppendLine();
        AppendPromptSectionStart(sb, "manifest_schema");
        sb.AppendLine("Required shape:");
        sb.AppendLine("name: generated-workflow-name");
        sb.AppendLine("description: Plain-language answer to: what does this workflow do for the user?");
        sb.AppendLine("# Optional only when obvious from the user request. Keep top-level inputs/outputs minimal.");
        sb.AppendLine("inputs: {}");
        sb.AppendLine("outputs: {}");
        sb.AppendLine("subplans:");
        sb.AppendLine("  - id: stable_unique_id");
        sb.AppendLine("    responsibility: |");
        sb.AppendLine("      Generation prompt for this sub-workflow.");
        sb.AppendLine("      Preserve the exact functional logic, constraints, branching rules, tool/provider requirements, language requirements, and output obligations from the user request that belong to this slice.");
        sb.AppendLine("      This text will be reused directly as the prompt to generate the sub-workflow.");
        sb.AppendLine("    algorithm:");
        sb.AppendLine("      type: sequence|parallel|foreach.sequential|foreach.parallel|switch|task|workflow.call");
        sb.AppendLine("      # Optional. When present, this subplan is a parent workflow generated after its children.");
        sb.AppendLine("algorithm:");
        sb.AppendLine("  type: sequence|parallel|foreach.sequential|foreach.parallel|switch|task|workflow.call");
        sb.AppendLine("Meta-language:");
        sb.AppendLine("- sequence uses steps: [algorithm nodes].");
        sb.AppendLine("- parallel uses branches: [algorithm nodes] or branches[].steps.");
        sb.AppendLine("- foreach.sequential and foreach.parallel use items, optional item_var/index_var, and steps.");
        sb.AppendLine("- switch uses optional expr, cases with value or when, and optional default.");
        sb.AppendLine("- task uses task: <short action>. Use task for simple inline orchestration or small local work that does not deserve its own generated workflow.");
        sb.AppendLine("- workflow.call uses only plan: <subplan id> and optional id. Do not emit args in the split manifest.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Split by algorithmic complexity, not by every sentence in the user request.");
        sb.AppendLine("- Create subplans for complex, repeated, tool-heavy, risky, or domain-cohesive blocks that need their own generated workflow.");
        sb.AppendLine("- Do not create a subplan for a tiny bookkeeping action, a one-branch response, a simple transformation, or a small glue step; use a task node inline in the main or parent algorithm.");
        sb.AppendLine("- Prefer deeper decomposition inside the most complex lifecycle block instead of many shallow top-level subplans.");
        sb.AppendLine("- The main algorithm may mix task nodes and workflow.call nodes. It does not have to call a subplan for every step.");
        sb.AppendLine("- The subplan responsibility is not a label. It is the generation prompt for that sub-workflow.");
        sb.AppendLine("- Write each responsibility as a concise but complete prompt that preserves the exact user-requested behavior for that functional slice.");
        sb.AppendLine("- Keep simple labels out of responsibility. Include all relevant branch rules, language rules, provider/tool constraints, side effects, and output/reporting obligations needed to generate that sub-workflow correctly.");
        sb.AppendLine("- Do not invent details, weaken constraints, or replace user logic with generic technical wording.");
        sb.AppendLine("- Do not declare inputs, outputs, constraints, data dependencies, schemas, or manifest-only implementation metadata on subplans.");
        sb.AppendLine("- Every leaf subplan must be independently generatable from its responsibility prompt plus the original user request.");
        sb.AppendLine("- A subplan may include its own algorithm to call other declared subplans. Use this for parent/child domain composition while keeping leaf subplans small.");
        sb.AppendLine("- Omit subplan.algorithm for leaf subplans. Never emit an empty algorithm such as `type: sequence` with no steps.");
        sb.AppendLine("- Subplan algorithms must not call themselves directly or indirectly.");
        sb.AppendLine("- Decompose by simple functional responsibilities. Think of this as a sequence diagram whose notes are faithful child-generation prompts.");
        sb.AppendLine("- Use only declared subplan ids in workflow.call nodes.");
        sb.AppendLine("- Parent subplans will be generated after their children; their prompt will include each generated child workflow YAML plus its inputs and outputs.");
        sb.AppendLine("- The manifest algorithm describes orchestration and dependencies only; executable argument mapping belongs to generated workflow YAML, not to the manifest.");
        sb.AppendLine("- Do not emit subplan paths. Safe relative paths are assigned deterministically by the runtime as ./<workflow-name>/<subworkflow-name>.yaml.");
        sb.AppendLine("Heuristic for issue-processing workflows:");
        sb.AppendLine("- Keep top-level orchestration compact: task/list/filter if simple, then foreach issue call a richer issue-lifecycle parent subplan.");
        sb.AppendLine("- Decompose the issue-lifecycle parent around meaningful complexity: classification, decision/action handling, implementation+PR, logging/cleanup if they are non-trivial.");
        sb.AppendLine("- In switch branches, use task nodes for simple comments/closures; reserve subplans for branches with substantial tool interaction or code changes.");
        AppendPromptSectionEnd(sb, "manifest_schema");
        sb.AppendLine();
        AppendUserTaskBlock(sb, instruction, context);
        return sb.ToString();
    }

    private static async Task<GeneratedSplitSubPlan> GenerateSubPlanAsync(
        StepExecutionContext ctx,
        JsonObject originalInput,
        JsonObject originalGenerator,
        string originalInstruction,
        string? originalContext,
        WorkflowPlanManifest manifest,
        WorkflowPlanSubPlan subPlan,
        IReadOnlyDictionary<string, GeneratedSplitSubPlan> generatedById,
        CancellationToken ct)
    {
        var subInput = new JsonObject();
        var dependencies = WorkflowPlanManifestDependencyPlanner.GetSubPlanDependencies(subPlan);
        var dependencyContracts = dependencies
            .Where(generatedById.ContainsKey)
            .Select(id => generatedById[id])
            .ToArray();

        if (originalInput["policy"] != null)
            subInput["policy"] = BuildSubPlanPolicy(originalInput["policy"] as JsonObject, dependencyContracts.Length > 0);
        if (originalInput["limits"] != null)
            subInput["limits"] = originalInput["limits"]!.DeepClone();
        if (originalInput["validate"] != null)
            subInput["validate"] = originalInput["validate"]!.DeepClone();
        if (originalInput["on_invalid"] != null)
            subInput["on_invalid"] = originalInput["on_invalid"]!.DeepClone();

        var generator = new JsonObject();
        CopyGeneratorOption(originalGenerator, generator, "provider");
        CopyGeneratorOption(originalGenerator, generator, "model");
        CopyGeneratorOption(originalGenerator, generator, "reasoning");
        CopyGeneratorOption(originalGenerator, generator, "prefilter");
        CopyGeneratorOption(originalGenerator, generator, "temperature");
        generator["instruction"] = BuildSubPlanInstruction(manifest, subPlan, dependencyContracts);
        generator["context"] = BuildSubPlanContext(manifest, subPlan, dependencyContracts, originalInstruction, originalContext);
        subInput["generator"] = generator;

        try
        {
            var result = await ExecuteSingleWorkflowPlanAsync(ctx, subInput, ct) as JsonObject
                ?? throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Sub-plan '{subPlan.Id}' did not return a workflow bundle.");

            var yaml = result["yaml"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(yaml))
                throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Sub-plan '{subPlan.Id}' did not return YAML.");

            var doc = ParseAndValidateGeneratedWorkflow(yaml);
            ValidateSplitWorkflowMinimumStepCount(doc, subPlan.Id);
            var contract = ExtractGeneratedSubPlanContract(doc);
            return new GeneratedSplitSubPlan(subPlan.Id, subPlan.Path, yaml, contract.Inputs, contract.Outputs);
        }
        catch (WorkflowRuntimeException ex)
        {
            throw new WorkflowRuntimeException(ex.Code, $"Sub-plan '{subPlan.Id}' generation failed: {ex.Message}", ex.Retryable);
        }
        catch (Exception ex)
        {
            throw new WorkflowRuntimeException(ErrorCodes.TemplatePlan, $"Sub-plan '{subPlan.Id}' generation failed: {ex.Message}");
        }
    }

    private static void CopyGeneratorOption(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] != null)
            destination[name] = source[name]!.DeepClone();
    }

    private static JsonObject? BuildSubPlanPolicy(JsonObject? originalPolicy, bool canCallGeneratedChildren)
    {
        if (originalPolicy == null)
            return null;

        var policy = originalPolicy.DeepClone().AsObject();
        if (!canCallGeneratedChildren)
            return policy;

        if (policy["allowed_step_types"] is JsonArray allowed)
        {
            if (!allowed.Any(static item => string.Equals(item?.GetValue<string>(), "workflow.call", StringComparison.Ordinal)))
                allowed.Add((JsonNode)JsonValue.Create("workflow.call")!);
        }

        if (policy["denied_step_types"] is JsonArray denied)
        {
            var filtered = new JsonArray(denied
                .Where(static item => !string.Equals(item?.GetValue<string>(), "workflow.call", StringComparison.Ordinal))
                .Select(static item => item?.DeepClone())
                .ToArray());
            policy["denied_step_types"] = filtered;
        }

        return policy;
    }

    private static string BuildSubPlanInstruction(
        WorkflowPlanManifest manifest,
        WorkflowPlanSubPlan subPlan,
        IReadOnlyList<GeneratedSplitSubPlan> dependencyContracts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generate only the sub-workflow '{subPlan.Id}' for split workflow '{manifest.Name}'.");
        if (dependencyContracts.Count == 0)
            sb.AppendLine("This is a leaf sub-workflow. Infer its own inputs and outputs from its generation prompt.");
        else
            sb.AppendLine("This is a parent sub-workflow. It must embed and orchestrate the generated child sub-workflows listed in context using local workflow.call.");
        sb.AppendLine("Keep the workflow as small as the generation prompt allows. Aim for fewer than 16 executable steps unless the feature genuinely needs more.");
        sb.AppendLine();
        sb.AppendLine("Sub-workflow generation prompt:");
        sb.AppendLine(subPlan.Responsibility);
        sb.AppendLine();
        sb.AppendLine("Workflow contract rules:");
        sb.AppendLine("- The document must include version, name, skill, and workflows.");
        sb.AppendLine("- The main workflow must declare the inputs it actually needs.");
        sb.AppendLine("- The main workflow outputs must expose useful outputs for callers.");
        sb.AppendLine($"- Every workflow in the returned YAML must contain at least {SplitMinimumWorkflowSteps} executable steps, counted recursively through containers.");
        if (dependencyContracts.Count > 0)
        {
            sb.AppendLine("- The YAML you return must be standalone: include the parent workflow plus the generated child workflows in the same top-level workflows map.");
            sb.AppendLine("- Copy each generated child workflow entrypoint into the returned document under the child id as workflow name.");
            sb.AppendLine("- If a child YAML contains additional local workflows, copy those too because the child entrypoint may call them.");
            sb.AppendLine("- Use workflow.call with ref.kind local and ref.name equal to the child id.");
            sb.AppendLine("- Do not use workflow.call ref.kind workspace or path refs in this parent.");
            sb.AppendLine("- Pass workflow.call input.args that match each child workflow's declared inputs.");
            sb.AppendLine("- Preserve declared types when passing child inputs: array inputs must receive JSON arrays, not comma-separated text or rendered strings.");
            sb.AppendLine("- A loop.sequential or loop.parallel items/over value must resolve to a JSON array. If data comes from text or an MCP response, normalize it to an array before looping or before passing it to an array input.");
            sb.AppendLine("- If the manifest guidance contains task nodes, implement those tasks directly inside this parent workflow instead of creating another local workflow for them.");
            sb.AppendLine("- Do not regenerate child logic inside this parent; call the children.");
        }
        return sb.ToString();
    }

    private static string BuildSubPlanContext(
        WorkflowPlanManifest manifest,
        WorkflowPlanSubPlan subPlan,
        IReadOnlyList<GeneratedSplitSubPlan> dependencyContracts,
        string originalInstruction,
        string? originalContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Original user request:");
        sb.AppendLine(originalInstruction);
        if (!string.IsNullOrWhiteSpace(originalContext))
        {
            sb.AppendLine();
            sb.AppendLine("Original user context:");
            sb.AppendLine(originalContext);
        }
        sb.AppendLine();
        sb.AppendLine("Use the original request only to preserve the exact rules relevant to this sub-workflow. Do not expand this sub-workflow beyond its generation prompt.");
        sb.AppendLine();
        sb.AppendLine("Global workflow inputs:");
        sb.AppendLine(manifest.Inputs.ToJsonString(PromptJsonOptions));
        sb.AppendLine();
        sb.AppendLine("Global workflow outputs:");
        sb.AppendLine(manifest.Outputs.ToJsonString(PromptJsonOptions));
        if (subPlan.Algorithm != null)
        {
            sb.AppendLine();
            sb.AppendLine("Manifest orchestration guidance for this parent sub-workflow:");
            sb.AppendLine(WorkflowPlanManifestCompiler.CompileAlgorithmYaml(subPlan.Algorithm));
        }
        if (dependencyContracts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Generated child workflows available to call:");
            var children = new JsonArray(dependencyContracts
                .OrderBy(static child => child.Id, StringComparer.Ordinal)
                .Select(static child => (JsonNode)new JsonObject
                {
                    ["id"] = child.Id,
                    ["inputs"] = child.Inputs.DeepClone(),
                    ["outputs"] = child.Outputs.DeepClone(),
                    ["yaml"] = child.Yaml
                })
                .ToArray());
            sb.AppendLine(children.ToJsonString(PromptJsonOptions));
        }
        return sb.ToString();
    }

    private static GeneratedSubPlanContract ExtractGeneratedSubPlanContract(WorkflowDocument doc)
    {
        var workflow = SelectGeneratedWorkflow(doc);
        return new GeneratedSubPlanContract(
            WorkflowInputsToJson(workflow.Inputs),
            WorkflowOutputsToJson(workflow.Outputs));
    }

    private static void ValidateSplitWorkflowMinimumStepCount(WorkflowDocument doc, string label)
    {
        foreach (var (workflowName, workflow) in doc.Workflows)
        {
            var stepCount = CountWorkflowSteps(workflow.Steps);
            if (stepCount < SplitMinimumWorkflowSteps)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.TemplatePlan,
                    $"Split workflow '{label}/{workflowName}' must contain at least {SplitMinimumWorkflowSteps} executable steps; found {stepCount}.");
            }
        }
    }

    private static int CountWorkflowSteps(IEnumerable<StepDef> steps)
    {
        var count = 0;
        foreach (var step in steps)
        {
            count++;
            if (step.Steps != null)
                count += CountWorkflowSteps(step.Steps);
            if (step.Branches != null)
            {
                foreach (var branch in step.Branches)
                    count += CountWorkflowSteps(branch.Steps);
            }
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                    count += CountWorkflowSteps(@case.Steps);
            }
            if (step.Default != null)
                count += CountWorkflowSteps(step.Default);
        }

        return count;
    }

    private static WorkflowDef SelectGeneratedWorkflow(WorkflowDocument doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.Entrypoint) && doc.Workflows.TryGetValue(doc.Entrypoint, out var entrypoint))
            return entrypoint;
        if (doc.Workflows.TryGetValue("main", out var main))
            return main;
        return doc.Workflows.Values.First();
    }

    private static JsonObject WorkflowInputsToJson(Dictionary<string, InputDef>? inputs)
    {
        var obj = new JsonObject();
        if (inputs == null)
            return obj;

        foreach (var (key, value) in inputs)
            obj[key] = InputDefToJson(value);
        return obj;
    }

    private static JsonObject WorkflowOutputsToJson(Dictionary<string, OutputDef>? outputs)
    {
        var obj = new JsonObject();
        if (outputs == null)
            return obj;

        foreach (var (key, value) in outputs)
            obj[key] = OutputDefToJson(value);
        return obj;
    }

    private static JsonObject InputDefToJson(InputDef input)
    {
        var obj = new JsonObject
        {
            ["type"] = input.Type,
            ["required"] = input.Required
        };
        if (!string.IsNullOrWhiteSpace(input.Description))
            obj["description"] = input.Description;
        if (input.Default != null)
            obj["default"] = JsonValue.Create(input.Default.ToString());
        if (input.Items != null)
            obj["items"] = InputDefToJson(input.Items);
        if (input.Properties != null)
            obj["properties"] = new JsonObject(input.Properties.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, InputDefToJson(kv.Value))));
        if (input.AdditionalProperties != null)
            obj["additional_properties"] = InputDefToJson(input.AdditionalProperties);
        if (input.RequiredProperties != null)
            obj["required_properties"] = new JsonArray(input.RequiredProperties.Select(static item => (JsonNode)JsonValue.Create(item)!).ToArray());
        return obj;
    }

    private static JsonObject OutputDefToJson(OutputDef output)
    {
        var obj = new JsonObject
        {
            ["type"] = output.Type
        };
        if (!string.IsNullOrWhiteSpace(output.Expr))
            obj["expr"] = output.Expr;
        if (!string.IsNullOrWhiteSpace(output.Description))
            obj["description"] = output.Description;
        if (output.Items != null)
            obj["items"] = OutputDefToJson(output.Items);
        if (output.Properties != null)
            obj["properties"] = new JsonObject(output.Properties.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, OutputDefToJson(kv.Value))));
        if (output.AdditionalProperties != null)
            obj["additional_properties"] = OutputDefToJson(output.AdditionalProperties);
        if (output.RequiredProperties != null)
            obj["required_properties"] = new JsonArray(output.RequiredProperties.Select(static item => (JsonNode)JsonValue.Create(item)!).ToArray());
        return obj;
    }

    private sealed record GeneratedSubPlanContract(JsonObject Inputs, JsonObject Outputs);

    private sealed record GeneratedSplitSubPlan(string Id, string Path, string Yaml, JsonObject Inputs, JsonObject Outputs);

    private static class WorkflowPlanSelfContainedComposer
    {
        public static string ComposeMainYaml(WorkflowPlanManifest manifest, IReadOnlyList<GeneratedSplitSubPlan> generatedSubPlans)
        {
            var manifestWithGeneratedContracts = BuildManifestWithGeneratedContracts(manifest, generatedSubPlans);
            var sb = new StringBuilder();
            sb.Append(WorkflowPlanManifestCompiler.CompileMainYaml(
                manifestWithGeneratedContracts,
                localCalls: true,
                minimumSteps: SplitMinimumWorkflowSteps).TrimEnd());
            sb.AppendLine();

            var appendedWorkflowNames = new HashSet<string>(StringComparer.Ordinal) { "main" };
            foreach (var generated in generatedSubPlans)
            {
                var doc = Parsing.WorkflowParser.Parse(generated.Yaml);
                AppendGeneratedWorkflows(sb, generated, doc, appendedWorkflowNames);
            }

            return sb.ToString();
        }

        private static void AppendGeneratedWorkflows(
            StringBuilder sb,
            GeneratedSplitSubPlan generated,
            WorkflowDocument doc,
            HashSet<string> appendedWorkflowNames)
        {
            if (doc.Workflows.TryGetValue(generated.Id, out var matchingWorkflow))
            {
                AppendWorkflowIfMissing(sb, generated.Id, matchingWorkflow, appendedWorkflowNames);
            }
            else
            {
                var selected = SelectGeneratedWorkflow(doc);
                AppendWorkflowIfMissing(sb, generated.Id, selected, appendedWorkflowNames);
            }

            foreach (var (name, workflow) in doc.Workflows)
            {
                if (string.Equals(name, "main", StringComparison.Ordinal))
                    continue;
                AppendWorkflowIfMissing(sb, name, workflow, appendedWorkflowNames);
            }
        }

        private static void AppendWorkflowIfMissing(
            StringBuilder sb,
            string name,
            WorkflowDef workflow,
            HashSet<string> appendedWorkflowNames)
        {
            if (!appendedWorkflowNames.Add(name))
                return;

            AppendWorkflow(sb, name, workflow, 2);
        }

        private static WorkflowPlanManifest BuildManifestWithGeneratedContracts(
            WorkflowPlanManifest manifest,
            IReadOnlyList<GeneratedSplitSubPlan> generatedSubPlans)
        {
            var generatedById = generatedSubPlans.ToDictionary(static plan => plan.Id, StringComparer.Ordinal);
            var result = new WorkflowPlanManifest
            {
                Name = manifest.Name,
                Description = manifest.Description,
                Inputs = manifest.Inputs.DeepClone().AsObject(),
                Outputs = manifest.Outputs.DeepClone().AsObject(),
                Algorithm = manifest.Algorithm,
                RawYaml = manifest.RawYaml,
                SubPlans = manifest.SubPlans.Select(plan =>
                {
                    generatedById.TryGetValue(plan.Id, out var generated);
                    return new WorkflowPlanSubPlan
                    {
                        Id = plan.Id,
                        Path = plan.Path,
                        Responsibility = plan.Responsibility,
                        Constraints = plan.Constraints.DeepClone().AsObject(),
                        Inputs = generated?.Inputs.DeepClone().AsObject() ?? plan.Inputs.DeepClone().AsObject(),
                        Outputs = generated?.Outputs.DeepClone().AsObject() ?? plan.Outputs.DeepClone().AsObject(),
                        Algorithm = plan.Algorithm
                    };
                }).ToList()
            };

            AddRootCalledGeneratedInputs(result, generatedById);
            return result;
        }

        private static void AddRootCalledGeneratedInputs(
            WorkflowPlanManifest manifest,
            IReadOnlyDictionary<string, GeneratedSplitSubPlan> generatedById)
        {
            var rootCalls = new HashSet<string>(StringComparer.Ordinal);
            WorkflowPlanManifestDependencyPlanner.CollectWorkflowCallPlans(manifest.Algorithm, rootCalls);

            foreach (var planId in rootCalls)
            {
                if (!generatedById.TryGetValue(planId, out var generated))
                    continue;

                foreach (var (name, input) in generated.Inputs)
                {
                    if (!manifest.Inputs.ContainsKey(name))
                        manifest.Inputs[name] = input?.DeepClone();
                }
            }
        }

        private static void AppendWorkflow(StringBuilder sb, string name, WorkflowDef workflow, int indent)
        {
            var pad = new string(' ', indent);
            sb.AppendLine($"{pad}{Quote(name)}:");
            if (workflow.Inputs is { Count: > 0 })
                AppendInputMapping(sb, $"{pad}  inputs", workflow.Inputs, indent + 2);
            if (!string.IsNullOrWhiteSpace(workflow.Functions))
                AppendBlockScalar(sb, $"{pad}  functions", workflow.Functions!, indent + 4);
            sb.AppendLine($"{pad}  steps:");
            foreach (var step in workflow.Steps)
                AppendStep(sb, step, indent + 4);
            if (workflow.Outputs is { Count: > 0 })
                AppendOutputMapping(sb, $"{pad}  outputs", workflow.Outputs, indent + 2);
        }

        private static void AppendStep(StringBuilder sb, StepDef step, int indent)
        {
            var pad = new string(' ', indent);
            sb.AppendLine($"{pad}- id: {Quote(step.Id)}");
            sb.AppendLine($"{pad}  type: {Quote(step.Type)}");
            if (!string.IsNullOrWhiteSpace(step.If))
                sb.AppendLine($"{pad}  if: {Quote(step.If!)}");
            if (!string.IsNullOrWhiteSpace(step.Output))
                sb.AppendLine($"{pad}  output: {Quote(step.Output!)}");
            if (!string.IsNullOrWhiteSpace(step.ItemVar))
                sb.AppendLine($"{pad}  item_var: {Quote(step.ItemVar!)}");
            if (!string.IsNullOrWhiteSpace(step.IndexVar))
                sb.AppendLine($"{pad}  index_var: {Quote(step.IndexVar!)}");
            if (!string.IsNullOrWhiteSpace(step.Expr))
                sb.AppendLine($"{pad}  expr: {Quote(step.Expr!)}");
            if (step.Input != null)
                AppendJsonValue(sb, $"{pad}  input", step.Input, indent + 2);
            if (step.Retry != null)
                AppendRetry(sb, step.Retry, $"{pad}  retry", indent + 2);
            if (step.OnError != null)
                AppendOnError(sb, step.OnError, $"{pad}  on_error", indent + 2);
            if (step.Branches is { Count: > 0 })
            {
                sb.AppendLine($"{pad}  branches:");
                foreach (var branch in step.Branches)
                {
                    sb.AppendLine($"{pad}    - steps:");
                    foreach (var child in branch.Steps)
                        AppendStep(sb, child, indent + 8);
                }
            }
            if (step.Cases is { Count: > 0 })
            {
                sb.AppendLine($"{pad}  cases:");
                foreach (var @case in step.Cases)
                {
                    sb.AppendLine($"{pad}    -");
                    if (!string.IsNullOrWhiteSpace(@case.Value))
                        sb.AppendLine($"{pad}      value: {Quote(@case.Value!)}");
                    if (!string.IsNullOrWhiteSpace(@case.When))
                        sb.AppendLine($"{pad}      when: {Quote(@case.When!)}");
                    sb.AppendLine($"{pad}      steps:");
                    foreach (var child in @case.Steps)
                        AppendStep(sb, child, indent + 8);
                }
            }
            if (step.Default is { Count: > 0 })
            {
                sb.AppendLine($"{pad}  default:");
                foreach (var child in step.Default)
                    AppendStep(sb, child, indent + 4);
            }
            if (step.Steps is { Count: > 0 })
            {
                sb.AppendLine($"{pad}  steps:");
                foreach (var child in step.Steps)
                    AppendStep(sb, child, indent + 4);
            }
        }

        private static void AppendInputMapping(StringBuilder sb, string label, IReadOnlyDictionary<string, InputDef> inputs, int indent)
        {
            sb.AppendLine($"{label}:");
            foreach (var (key, value) in inputs)
                AppendJsonProperty(sb, key, InputDefToJson(value), indent + 2);
        }

        private static void AppendOutputMapping(StringBuilder sb, string label, IReadOnlyDictionary<string, OutputDef> outputs, int indent)
        {
            sb.AppendLine($"{label}:");
            foreach (var (key, value) in outputs)
            {
                if (!string.IsNullOrWhiteSpace(value.Expr) && IsPlainExpressionOutput(value))
                    sb.AppendLine($"{new string(' ', indent + 2)}{key}: {Quote(value.Expr)}");
                else
                    AppendJsonProperty(sb, key, OutputDefToJson(value), indent + 2);
            }
        }

        private static bool IsPlainExpressionOutput(OutputDef output)
            => output.Type == "any"
                && string.IsNullOrWhiteSpace(output.Description)
                && output.Items == null
                && output.Properties == null
                && output.AdditionalProperties == null
                && output.RequiredProperties == null;

        private static void AppendRetry(StringBuilder sb, RetryPolicy retry, string label, int indent)
        {
            sb.AppendLine($"{label}:");
            var pad = new string(' ', indent + 2);
            sb.AppendLine($"{pad}max: {retry.Max}");
            sb.AppendLine($"{pad}backoff_ms: {retry.BackoffMs}");
            sb.AppendLine($"{pad}backoff_mult: {retry.BackoffMult.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"{pad}jitter_ms: {retry.JitterMs}");
        }

        private static void AppendOnError(StringBuilder sb, OnErrorDef onError, string label, int indent)
        {
            sb.AppendLine($"{label}:");
            sb.AppendLine($"{new string(' ', indent + 2)}cases:");
            foreach (var @case in onError.Cases)
            {
                var pad = new string(' ', indent + 4);
                sb.AppendLine($"{pad}-");
                if (!string.IsNullOrWhiteSpace(@case.If))
                    sb.AppendLine($"{pad}  if: {Quote(@case.If!)}");
                sb.AppendLine($"{pad}  action: {Quote(@case.Action)}");
                if (@case.SetOutput != null)
                    AppendJsonValue(sb, $"{pad}  set_output", @case.SetOutput, indent + 6);
                if (@case.Retry != null)
                    AppendRetry(sb, @case.Retry, $"{pad}  retry", indent + 6);
            }
        }

        private static void AppendBlockScalar(StringBuilder sb, string label, string value, int indent)
        {
            sb.AppendLine($"{label}: |");
            var pad = new string(' ', indent);
            foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                sb.AppendLine(pad + line);
        }

        private static void AppendJsonProperty(StringBuilder sb, string key, JsonNode? value, int indent)
            => AppendJsonValue(sb, new string(' ', indent) + key, value, indent);

        private static void AppendJsonValue(StringBuilder sb, string label, JsonNode? value, int indent)
        {
            switch (value)
            {
                case JsonObject obj:
                    sb.AppendLine($"{label}:");
                    foreach (var (key, child) in obj)
                        AppendJsonProperty(sb, key, child, indent + 2);
                    break;
                case JsonArray arr:
                    sb.AppendLine($"{label}:");
                    foreach (var item in arr)
                        AppendJsonArrayItem(sb, item, indent + 2);
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                    sb.AppendLine($"{label}: {Quote(stringValue)}");
                    break;
                case JsonValue:
                    sb.AppendLine($"{label}: {value!.ToJsonString(PromptJsonOptions)}");
                    break;
                default:
                    sb.AppendLine($"{label}: null");
                    break;
            }
        }

        private static void AppendJsonArrayItem(StringBuilder sb, JsonNode? value, int indent)
        {
            var pad = new string(' ', indent);
            switch (value)
            {
                case JsonObject obj:
                    sb.AppendLine($"{pad}-");
                    foreach (var (key, child) in obj)
                        AppendJsonProperty(sb, key, child, indent + 2);
                    break;
                case JsonArray arr:
                    sb.AppendLine($"{pad}-");
                    foreach (var item in arr)
                        AppendJsonArrayItem(sb, item, indent + 2);
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                    sb.AppendLine($"{pad}- {Quote(stringValue)}");
                    break;
                case JsonValue:
                    sb.AppendLine($"{pad}- {value!.ToJsonString(PromptJsonOptions)}");
                    break;
                default:
                    sb.AppendLine($"{pad}- null");
                    break;
            }
        }

        private static string Quote(string value)
        {
            var escaped = value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            return "\"" + escaped + "\"";
        }
    }
}
