using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DataflowBindingTests
{
    [Theory]
    [InlineData("observe", "first", "second", "third")]
    [InlineData("observer", "premier", "deuxieme", "troisieme")]
    public async Task ReadCollectionPreservesEveryResultWithoutInventingInterToolArguments(string operation, string first, string second, string third)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "resource", Schema = new() { Type = "string" } }];
        workflow.Outputs.Clear();
        var input = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["resource"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("resource"),
            ["additionalProperties"] = false
        };
        var output = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("value")
        };
        var names = new[] { first, second, third }; var observed = new List<string>();
        var factory = new InMemoryMcpClientFactory();
        foreach (var name in names)
        {
            preparation.Capabilities.Add(new()
            {
                Id = name,
                StepType = "mcp.call",
                Server = name,
                Method = "inspect",
                Kind = "tool",
                EffectKind = "read",
                OperationIds = [operation],
                InputSchema = (JsonObject)input.DeepClone(),
                OutputSchema = (JsonObject)output.DeepClone()
            });
            factory.RegisterServer(name, new()
            {
                Tools = [new() { Name = "inspect", InputSchema = input, OutputSchema = output }],
                ToolHandlers = new()
                {
                    ["inspect"] = args =>
                    {
                        var value = args!["resource"]!.ToString() + ":" + name; observed.Add(value);
                        Assert.Single(args.AsObject()); return new McpCallResult { Content = new JsonObject { ["value"] = value } };
                    }
                }
            });
        }
        var group = new PlanningNode
        {
            Key = "observations",
            Type = "sequence",
            OperationIds = [operation],
            Steps = names.Select(name =>
            new PlanningNode
            {
                Key = name,
                Type = "mcp.call",
                CapabilityId = name,
                OperationIds = [operation],
                Input = Obj(("request", Obj(("resource", new() { Kind = "input", Source = "resource" }))))
            }).ToList()
        };
        workflow.Steps = [group];
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, preparation));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        foreach (var resource in new[] { "alpha", "autre-ressource" })
        {
            observed.Clear();
            var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["resource"] = resource }, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(names.Select(name => resource + ":" + name), observed);
            var collected = Assert.Single(result.StepResults).Output!.AsObject();
            Assert.Equal(3, collected.Count);
            Assert.All(observed, value => Assert.Contains(value, collected.ToJsonString()));
        }
        preparation.Capabilities[^1].InputOperationIds = ["required_observation"];
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, preparation), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
        preparation.Capabilities[^1].InputOperationIds.Clear();
        foreach (var effect in new[] { "unknown", "execute", "write", "lifecycle" })
        {
            preparation.Capabilities[^1].EffectKind = effect;
            Assert.Contains(PlanningDataflow.OperationInputFindings(graph, preparation), d => d.Code == "COMPOSITION_INPUT_BINDING_MISSING");
        }
    }

    [Theory]
    [InlineData("inspect", "pages", "finish", "loop.sequential", false)]
    [InlineData("analyser", "pages_renommees", "terminer", "loop.parallel", false)]
    [InlineData("inspect", "pages", "finish", "loop.sequential", true)]
    [InlineData("analyser", "pages_renommees", "terminer", "loop.parallel", true)]
    public async Task OwnedIterationKeepsCompositeDependenciesAtTheFinalConsumer(string operation, string loopKey, string lastKey, string loopType, bool localContainers)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities = [new() { Id = "first", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] },
            new() { Id = "last", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] }];
        PlanningValue Ref(string key) => new() { Kind = "output", Source = key };
        var first = new PlanningNode { Key = "page", CapabilityId = "first", OperationIds = [operation], Input = Obj(("resource", Ref("resource"))) };
        var pages = new PlanningNode { Key = loopKey, Type = loopType, OperationIds = [operation], Steps = [first] };
        var last = new PlanningNode { Key = lastKey, CapabilityId = "last", OperationIds = [operation], Input = Obj(("pages", Ref(loopKey)), ("analysis", Ref("analysis"))) };
        var group = new PlanningNode { Key = "owned", Type = "sequence", OperationIds = [operation], Steps = [pages, last] };
        if (localContainers)
        {
            prep.Capabilities.Add(new() { Id = "local-container", StepType = "set", Resolution = "local", EffectKind = "none", OperationIds = [operation] });
            group.CapabilityId = pages.CapabilityId = "local-container";
        }
        workflow.Steps = [new() { Key = "resource", OperationIds = ["resource"] }, new() { Key = "analysis", OperationIds = ["analysis"] }, group];
        Assert.Empty(PlanningOperationCompositions.RequiredInputs(workflow, first, prep));
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        pages.Input = Obj(("items", new() { Kind = "array", Items = [Str("first"), Str("second")] }));
        workflow.Steps[0].Input = Obj(("label", Str("original resource")));
        workflow.Steps[1].Input = Obj(("label", Str("independent observation")));
        workflow.Outputs.Clear(); prep.AllowedStepTypes.AddRange([loopType, "sequence"]);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        var execution = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(execution.Success, execution.Error?.Message);
        var final = execution.StepResults[^1].Output!;
        Assert.Contains("original resource", final.ToJsonString());
        Assert.Contains("independent observation", final.ToJsonString());
        last.Input = Obj(("pages", Ref(loopKey)));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Location == "/workflows/0/steps/2/steps/1/input" && d.Message.Contains("analysis", StringComparison.Ordinal));
        last.Input = Obj(("resource", Ref("resource")), ("analysis", Ref("analysis")));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "COMPOSITION_INPUT_BINDING_MISSING" && d.Message.Contains(loopKey, StringComparison.Ordinal));
        last.Input = Obj(("pages", Ref(loopKey)), ("analysis", Ref("analysis")));
        pages.If = new() { Kind = "boolean", Boolean = true };
        Assert.Null(PlanningOperationCompositions.Owner(workflow, first, prep));
        pages.If = null; first.OperationIds = ["unrelated"];
        Assert.Null(PlanningOperationCompositions.Owner(workflow, first, prep));
        first.OperationIds = [operation]; first.Type = "human.input";
        Assert.Null(PlanningOperationCompositions.Owner(workflow, first, prep));
        if (localContainers)
        {
            first.Type = "set";
            prep.Capabilities[^1].InputOperationIds = ["unobserved"];
            Assert.Contains("unobserved", PlanningOperationCompositions.RequiredInputs(workflow, last, prep));
            prep.Capabilities[^1].OperationIds = ["unrelated"];
            Assert.Null(PlanningOperationCompositions.Owner(workflow, first, prep));
            prep.Capabilities[^1].OperationIds = [operation]; prep.Capabilities[^1].EffectKind = "write";
            Assert.Null(PlanningOperationCompositions.Owner(workflow, first, prep));
        }
    }

    [Theory]
    [InlineData("inspect", "prepare", "finish")]
    [InlineData("analyser", "preparer", "terminer")]
    public void ComposedOperationConsumesAllDependenciesAtItsTerminalAndEveryIntermediate(string operation, string firstKey, string lastKey)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities = [new() { Id = "first", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] },
            new() { Id = "last", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] }];
        PlanningValue Ref(string key) => new() { Kind = "output", Source = key };
        var first = new PlanningNode { Key = firstKey, CapabilityId = "first", OperationIds = [operation], Input = Obj(("resource", Ref("resource"))) };
        var last = new PlanningNode { Key = lastKey, CapabilityId = "last", OperationIds = [operation], Input = Obj(("prepared", Ref(firstKey)), ("analysis", Ref("analysis"))) };
        var group = new PlanningNode { Key = "owned", Type = "sequence", OperationIds = [operation], Steps = [first, last] };
        workflow.Steps = [new() { Key = "resource", OperationIds = ["resource"] }, new() { Key = "analysis", OperationIds = ["analysis"] }, group];
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        last.Input = Obj(("resource", Ref("resource")), ("analysis", Ref("analysis")));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "COMPOSITION_INPUT_BINDING_MISSING" && d.Message.Contains(firstKey, StringComparison.Ordinal));
        last.Input = Obj(("prepared", Ref(firstKey)));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Message.Contains("analysis", StringComparison.Ordinal));
        last.Input = Obj(("prepared", Ref(firstKey)), ("analysis", Ref("analysis")));
        first.If = new() { Kind = "boolean", Boolean = true };
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Location == "/workflows/0/steps/2/steps/0/input");
        first.If = null; group.OperationIds = ["unrelated_owner"];
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Location == "/workflows/0/steps/2/steps/0/input");
    }

    [Theory]
    [InlineData("reader", "parallel")]
    [InlineData("lecteur_renomme", "lectures")]
    public void ParallelBindingsExposeExactBranchProducersAndPreserveRawProvenance(string name, string group)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities.Add(new()
        {
            Id = "source",
            StepType = "mcp.call",
            OutputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["location"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("location")
            }
        });
        var read = new PlanningNode
        {
            Key = name,
            Type = "mcp.call",
            CapabilityId = "source",
            StructuredOutput = new(new()
            {
                Type = "object",
                Properties = [new() { Name = "summary", Required = true, Schema = new() { Type = "string" } }]
            })
        };
        workflow.Steps.Insert(0, new() { Key = group, Type = "parallel", Branches = [new([read]), new([new() { Key = "other", Input = Obj(("unrelated", Str("value"))) }])] });
        var bindings = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values;
        var raw = Assert.Single(bindings, b => b.Value.Source == group && b.Value.Path.SequenceEqual(new[] { "branches", "0", name, "response", "location" }));
        Assert.Equal("string", raw.Schema["type"]!.ToString()); Assert.Equal("unconditional", raw.Availability);
        Assert.Contains(bindings, b => b.Value.Path.SequenceEqual(new[] { "branches", "0", name, "json", "summary" }));
        Assert.DoesNotContain(bindings, b => b.Value.Source == name); // completed group is the boundary
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, prep);
        Assert.Throws<InvalidOperationException>(() => resolve(new() { Kind = "output", Source = group, Path = ["branches", "1", name, "response", "location"] }));
        Assert.Throws<InvalidOperationException>(() => resolve(new() { Kind = "output", Source = group, Path = ["branches", "2"] }));
        Assert.True(PlanningValueProvenance.Proves(workflow, raw.Value, graph, (node, value) => node == read && value.Path.SequenceEqual(new[] { "location" })));
        Assert.False(PlanningValueProvenance.Proves(workflow, new() { Kind = "output", Source = group, Path = ["branches", "0", name, "json", "summary"] }, graph, (node, _) => node == read));
        read.OnError = [new(null, "continue", Obj(("error", Str("unavailable"))), null)];
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Path.SequenceEqual(raw.Value.Path));
    }

    private static (PlanningGraph Graph, PlanningPreparation Preparation) Fixture()
    {
        var graph = Graph(); var prep = Preparation();
        graph.Workflows[0].Inputs.Add(new() { Name = "resource", Schema = new() { Type = "string" } });
        graph.Workflows[0].Steps.Insert(0, new() { Key = "read", Type = "mcp.call", CapabilityId = "reader", Input = Obj(("request", Obj(("resource", new() { Kind = "input", Source = "resource" })))) });
        prep.Capabilities.Add(new()
        {
            Id = "reader",
            StepType = "mcp.call",
            Server = "renamed",
            Method = "inspect",
            Kind = "tool",
            OutputSchema = new(),
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"resource":{"type":"string"},"optional":{"type":["string","null"]}},"required":["resource"],"additionalProperties":false}""")!.AsObject()
        });
        return (graph, prep);
    }

    [Fact]
    public void ConditionalChildIsUnavailableOutsideItsBranch()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var read = workflow.Steps[0];
        workflow.Steps[0] = new() { Key = "choose", Type = "switch", Expr = Str("take"), Cases = [new("take", null, [read])], Default = [] };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        read.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "known", Schema = new() { Type = "string" } }] });
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Fact]
    public async Task IncompleteModelOutputIsReportedAsACompletionLimit_NotMalformedIntent()
    {
        var state = Session();
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Requests);
        Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_OUTPUT_LIMIT"); Assert.Null(state.ApprovedBehaviorHash);
    }

    [Fact]
    public void RequiredNativeDecisionCannotBeReplacedByAnUnboundLocalComputation()
    {
        var behavior = BehaviorPlan(); var prep = Preparation();
        prep.Capabilities.Add(new() { Id = "evaluate", StepType = "decision.evaluate", Resolution = "native", Required = true, OperationIds = ["assess"] });
        behavior.Workflows[0].OperationIds.Add("assess");
        behavior.Workflows[0].Steps.Add(new() { Key = "assess", Kind = "operation", Purpose = "Assess runtime evidence", OperationIds = ["assess"] });
        Assert.Contains(PlanningBehaviorPlans.Validate(behavior, prep), d => d.Message.Contains("evaluate", StringComparison.Ordinal));
        behavior.Workflows[0].Steps[^1].CapabilityId = "evaluate";
        Assert.DoesNotContain(PlanningBehaviorPlans.Validate(behavior, prep), d => d.Message.Contains("evaluate", StringComparison.Ordinal));
    }

    [Fact]
    public void GuardedProducerRequiresTheSameEstablishedGuard()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[0].If = new() { Kind = "input", Source = "enabled" };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        workflow.Steps[1].If = new() { Kind = "input", Source = "enabled" };
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Fact]
    public void ConditionalSequenceGuardAppliesToItsChildProducers()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var read = workflow.Steps[0];
        workflow.Steps[0] = new() { Key = "guarded", Type = "sequence", If = new() { Kind = "input", Source = "enabled" }, Steps = [read] };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        Assert.All(PlanningDataflow.Index(workflow, prep, graph).Values.Where(b => b.Value.Source == "read"), b => Assert.Equal("conditional", b.Availability));
        workflow.Steps[1].If = new() { Kind = "input", Source = "enabled" };
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Theory]
    [InlineData("value.toUpperCase()")]
    [InlineData("const result = value.toUpperCase(); return result;")]
    [InlineData("return [[value]].map(([entry]) => entry.toUpperCase())[0];")]
    [InlineData("const { entry: result } = { entry: value }; return result.toUpperCase();")]
    [InlineData("return (({ entry = '' }, ...suffix) => entry.toUpperCase() + suffix.join(''))({ entry: value });")]
    [InlineData("try { throw new Error(value); } catch (error) { const { message } = error; return message.toUpperCase(); } return '';")]
    [InlineData("decodeURIComponent(encodeURIComponent(value)).toUpperCase()")]
    public async Task NamedComputationParametersExecuteWithoutImplicitContext(string expression)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = expression, Members = [new("value", new() { Kind = "input", Source = "source" })] }));
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, Preparation())));
        var run = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["source"] = "hello" }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("HELLO", run.Outputs?["message"]?.ToString());
        workflow.Steps[0].Input.Members[0].Value.Text = "data.inputs.source";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_BINDING_INVALID");
    }

    [Fact]
    public async Task NestedTemplatesRemainStrings_WhenUsedAsComputationArgumentsAndOutputs()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        var nested = new PlanningValue
        {
            Kind = "template",
            Text = "prefix: {{inner}}",
            Members =
            [new("inner", new() { Kind = "template", Text = "[{{value}}]", Members = [new("value", new() { Kind = "input", Source = "source" })] })]
        };
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = "value.toUpperCase()", Members = [new("value", nested)] }));
        workflow.Outputs[0].Value = new() { Kind = "template", Text = "{{result}}!", Members = [new("result", workflow.Outputs[0].Value)] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, Preparation())));
        var run = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["source"] = "hello" }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("PREFIX: [HELLO]!", run.Outputs?["message"]?.ToString());
    }

    [Theory]
    [InlineData("analysis", "select")]
    [InlineData("analyse", "sélection")]
    public void LockedProducerDependencyRejectsUnrelatedData_AndAcceptsItsAlias(string sourceOperation, string consumerOperation)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        prep.Capabilities[0].OperationIds = [sourceOperation];
        prep.Capabilities.Add(new() { Id = "local", StepType = "set", OperationIds = [consumerOperation], InputOperationIds = [sourceOperation] });
        workflow.Steps[1].CapabilityId = "local";
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "input", Source = "resource" }));
        var finding = Assert.Single(PlanningDataflow.OperationInputFindings(graph, prep));
        Assert.Equal("/workflows/0/steps/1/input", finding.Location); Assert.Contains("read", finding.Message);
        workflow.Steps.Insert(1, new() { Key = "alias", Input = Obj(("value", new() { Kind = "output", Source = "read" })) });
        workflow.Steps[2].Input = Obj(("message", new() { Kind = "output", Source = "alias", Path = ["value"] }));
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
    }

    [Fact]
    public void RuntimeArgumentLocationsRespectRetainedTransportMembers()
    {
        var (graph, _) = Fixture(); var node = graph.Workflows[0].Steps[0];
        node.Input.Members.Insert(0, new("raise_on_error", new() { Kind = "boolean", Boolean = false }));
        Assert.Equal("/workflows/0/steps/0/input/members/1/value/members/0/value", PlanningExecutableValidation.MapRuntimeDiagnostic(new("TEST", "/workflows/0/steps/0/input/request/resource", "Test"), graph).Location);
    }

    [Fact]
    public void InvalidNestedValuesAreLocatedBeforeFullCompilation()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = "invented";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "VALUE_LOWERING_INVALID" && d.Location == "/workflows/0/steps/0/input");
    }

    [Fact]
    public void AcceptedInputDependenciesRejectHardCodedExamples()
    {
        var graph = Graph(); var behavior = BehaviorPlan();
        behavior.Workflows[0].Inputs.Add(new("source", "Dynamic message", true));
        behavior.Workflows[0].Steps[0].InputDependencies = ["source"];
        graph.Workflows[0].Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, Preparation()), d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
        graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "input", Source = "source" }));
        Assert.Empty(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, Preparation()));
        var snapshot = Session(); snapshot.Graph = graph; snapshot.Construction.Dataflow = PlanningDataflow.Describe(graph, Preparation());
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(snapshot.Construction.Dataflow.Fingerprint, restored.Construction.Dataflow!.Fingerprint);
    }

    [Theory]
    [InlineData("'example'")]
    [InlineData("void source; return 'example';")]
    [InlineData("source; return 'example';")]
    [InlineData("(() => { const source = 'example'; return source; })()")]
    public void DeclaringAnUnusedOrShadowedInputDoesNotEstablishDynamicDataflow(string expression)
    {
        var value = new PlanningValue { Kind = "compute", Text = expression, Members = [new("source", new() { Kind = "input", Source = "url" })] };
        Assert.Throws<InvalidOperationException>(() => PlanningComputations.Validate(value));
    }

    [Fact]
    public void RawBindingIsUnavailableWhenContinuationOnlyProducesStructuredJson()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0];
        node.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "ok", Required = true, Schema = new() { Type = "boolean" } }] });
        node.OnError = [new(null, "continue", Obj(("json", Obj(("ok", new() { Kind = "boolean", Boolean = false })))), null)];
        var bindings = PlanningDataflow.Index(workflow, prep, graph, "greeting");
        Assert.DoesNotContain(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel is null or "default");
        Assert.Contains(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "structured");
        Assert.Contains(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "envelope" && b.Value.Path.Count == 0);
        Assert.DoesNotContain(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "envelope" && b.Value.Path.FirstOrDefault() == "response");
        node.OnError[0].SetOutput!.Members.Add(new("response", Obj()));
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == node.Key && b.Value.ResultChannel is null or "default");
    }
}
