using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RetainedCompositionRequestTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("v13")]
    [InlineData("v14")]
    [InlineData("v15")]
    public void RecordedResponsesKeepTheirAcceptanceAndExposeThePresentationGap(string version)
    {
        var fixture = Read(version);
        var issued = fixture["request"]!.Deserialize(PlanningJsonContext.Default.LLMRequest)!;
        var response = fixture["response"]!;
        var storedRequest = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, issued.StructuredOutputSchema!));
        Assert.Contains("typed copying", issued.Prompt);
        Assert.All(PlanningSchemaReferences.Walk(issued.StructuredOutputSchema!.AsObject(), "", 0),
            entry => Assert.False(entry.Schema.ContainsKey("description")));
        var context = JsonNode.Parse(issued.Prompt[(issued.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        var state = PlannerFixture.Session();
        state.Request.Prompt = context["request"]!.ToString();
        state.Request.Generation.MaxInputTokensPerRequest = 96000;
        state.Request.Options = fixture["options"]!.DeepClone().AsObject();
        state.Catalog = fixture["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!;
        state.Requirements = context["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!;
        state.Plan = response["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        // An annotation is advice, not a new semantic checker. The same malformed
        // TaskPlan value must still fail against both complete issued schemas.
        var malformed = response.DeepClone();
        malformed["plan"]!["root"]!["tasks"]![0]!["requires"] = new JsonObject { ["kind"] = "made_up" };
        var historicalFindings = PlanningContractValidation.ValidateInstanceFindings(malformed, issued.StructuredOutputSchema!);
        Assert.NotEmpty(historicalFindings);

        var tasks = TaskPlanRevisions.Tasks(state.Plan).ToArray();
        Assert.DoesNotContain(tasks, t => t.Each is not null);
        Assert.Contains(tasks, t => t.Kind == "transform" && t.Mode == "interpret" && t.Id.Contains(version == "v15" ? "xlsx_rows" : version == "v14" ? "workbook_rows" : "excel_rows", StringComparison.Ordinal));
        Assert.Contains(tasks, t => t.Kind == "transform" && t.Requires is null && t.Inputs.Any(i => i.Value.Kind == "output" && i.Value.Port == (version == "v13" ? null : "observationSnapshot")));
        if (version != "v14") Assert.Contains(tasks, t => t.Id == "click_preference_accept");
        if (version == "v15")
        {
            var visit = tasks.Single(t => t.Id == "visit_products").Body!.Tasks;
            var open = visit.Single(t => t.Id == "open_product_page");
            var absent = visit.Single(t => t.Id == "record_missing_url_product");
            Assert.True(visit.IndexOf(open) < visit.IndexOf(absent));
            Assert.Equal("not_equal", open.Requires!.Predicate); Assert.Equal("equal", absent.Requires!.Predicate);
            Assert.Equal(JsonSerializer.Serialize(open.Requires.Items, PlanningJsonContext.Default.ListTaskValue),
                JsonSerializer.Serialize(absent.Requires.Items, PlanningJsonContext.Default.ListTaskValue));
        }
        var compiled = new TaskPlanCompiler().Compile(state.Plan, state.Catalog, state.Request);
        if (version == "v15") Assert.Empty(compiled.Diagnostics);
        else if (version == "v14") Assert.Equal(("TASK_INPUT_TYPE", "/tasks/write_xlsx/inputs/rows"),
            (Assert.Single(compiled.Diagnostics).Code, compiled.Diagnostics[0].Location));
        else
        {
            Assert.Equal(14, compiled.Diagnostics.Count);
            Assert.All(compiled.Diagnostics, d => Assert.Equal("TASK_FIELD_TYPE", d.Code));
        }
        output.WriteLine($"{version}: original request={fixture["original_request_sha256"]}; receipt={fixture["original_receipt_sha256"]}; tasks={tasks.Length}; compiler=[{string.Join(',', compiled.Diagnostics.Select(d => d.Code + ":" + d.Location))}]; schema-valid unsuitable composition remains a review concern.");
        Assert.Equal(storedRequest, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
    }

    internal static JsonNode Read(string version) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "InitialComposition", "retained-" + version + ".json")))!;
}
