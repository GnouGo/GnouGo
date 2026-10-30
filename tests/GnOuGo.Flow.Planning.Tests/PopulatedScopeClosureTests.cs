using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PopulatedScopeClosureTests
{
    [Theory]
    [InlineData("conditional", false)]
    [InlineData("conditional", true)]
    [InlineData("parallel", false)]
    [InlineData("parallel", true)]
    public async Task SuppliedControlClosesItsIntentionallyEmptyBranch(string kind, bool prerequisite)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = kind;
        if (prerequisite) step.Prerequisites = [new() { Need = "An unrelated future result is unavailable.", EvidenceAddresses = ["requirement:work"] }];
        var control = new PlanTask { Id = "choose", Kind = kind, Objective = "Calculate only when requested." };
        if (kind == "conditional")
        {
            control.Condition = new() { Kind = "boolean", Boolean = true };
            control.Body = new() { Tasks = [Value("calculate")] }; control.Otherwise = new();
        }
        else control.Branches = [new() { Tasks = [Value("calculate")] }, new()];
        var candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], Tasks = [control]
        });
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Equal(["/root"], candidate.OpenScopes);
        Assert.Equal(kind == "conditional" ? ["/tasks/choose/body", "/tasks/choose/otherwise"] :
            new[] { "/tasks/choose/branches/0", "/tasks/choose/branches/1" }, candidate.Extension.CloseScopes);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyContinuationOrPrerequisiteOpeningAndItsAncestorsStayOpen(bool prerequisite)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "group";
        if (prerequisite) step.Prerequisites = [new() { Need = "The required numeric result is unavailable.", EvidenceAddresses = ["requirement:work"] }];
        var extension = new PlanningConstructionExtension
        {
            Scope = "/root", RequirementIds = ["work"], ContinueInScope = prerequisite ? null : "/tasks/unfinished/body",
            Groups = [new()
            {
                Id = "worker", Body = new() { Tasks =
                [
                    new() { Id = "unfinished", Kind = "sequence", Objective = "Retain work requiring another fragment.",
                        Body = new() { Tasks = prerequisite ? [] : [Value("first")] } },
                    new() { Id = "finished", Kind = "sequence", Objective = "Complete independent work.", Body = new() { Tasks = [Value("second")] } }
                ] }
            }]
        };
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Equal(["/tasks/unfinished/body", "/groups/worker/body", "/root"], candidate.OpenScopes);
        Assert.Equal(["/tasks/finished/body"], candidate.Extension.CloseScopes);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        candidate.Plan.Groups[0].Body.Tasks.Add(new() { Id = "consumer", Kind = "value", Objective = "Cannot observe an unfinished boundary.",
            DependsOn = ["unfinished"], Outputs = [new("complete", new() { Kind = "present", Source = "unfinished" })] });
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics, diagnostic => diagnostic.Code == "TASK_SCOPE_OPEN");
    }

    [Fact]
    public async Task NewCompletedScopeDoesNotCloseAnExistingOwningBoundary()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Root.Tasks.Add(new() { Id = "outer", Kind = "sequence", Objective = "Existing unfinished responsibility.", Body = new() });
        state.Construction.OpenScopes.Add("/tasks/outer/body");
        var step = state.Construction.Compact!.CurrentStep!;
        step.Scope = "/tasks/outer/body"; step.Kind = "sequence"; step.Inputs.Clear();
        var candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = step.Scope, RequirementIds = ["work"], Tasks = [new()
            {
                Id = "inner", Kind = "sequence", Objective = "Complete this newly supplied child.", Body = new() { Tasks = [Value("result")] }
            }]
        });
        Assert.Contains("/tasks/outer/body", candidate.OpenScopes);
        Assert.Contains("/root", candidate.OpenScopes);
        Assert.Equal(["/tasks/inner/body"], candidate.Extension.CloseScopes);
    }

    private static PlanTask Value(string id) => new() { Id = id, Kind = "value", Objective = "Produce an exact numeric result.",
        Outputs = [new("number", new() { Kind = "number", Number = 1 })] };
}
