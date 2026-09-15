using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class OperationOccurrenceTests
{
    [Fact]
    public void InterpretationCannotSelectOccurrenceIdentityOrForeignClauseSubject()
    {
        var state = OperationAdmissionTests.State("Transform an entry. Apply its rules.");
        var scope = PlanningOperations.SourceScopes(state)[0];
        var schema = PlanningOperations.RuntimeSchema(state, PlanningSourceAuthority.RequestedBehavior, scope.Boundaries);
        var value = new JsonObject { ["role"] = "local_behavior", ["kind"] = "local_processing", ["action"] = new JsonObject { ["start"] = "b0", ["end"] = "b3" },
            ["execution"] = "generated_workflow", ["evidence"] = "action", ["necessity"] = new JsonObject { ["state"] = "unspecified", ["evidence"] = null }, ["baseline"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        value["subject"] = PlanningOperations.SourceScopes(state)[1].Clause.Id;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        value.Remove("subject"); value["occurrence"] = "distinct";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonArray(value.DeepClone()), schema));
        Assert.All(PlanningSourceDecisions.InterpretationDecisions(state), d => Assert.Null(d.Context["subjects"]));
    }

}
