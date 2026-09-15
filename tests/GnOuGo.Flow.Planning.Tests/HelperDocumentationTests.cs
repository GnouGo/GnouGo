using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HelperDocumentationTests
{

    [Theory]
    [InlineData("function helper(v) { return v; }", "FUNCTION_JSDOC_MISSING")]
    [InlineData("/** @returns {string} Text */ function helper(v) { return v; }", "FUNCTION_JSDOC_PARAM_MISSING")]
    [InlineData("/** @param {string} v Text */ function helper(v) { return v; }", "FUNCTION_JSDOC_RETURNS_MISSING")]
    public void ConstructionUsesTheRuntimeDocumentationRequirements(string script, string code)
    {
        var finding = Assert.Single(GeneratedFunctionDocumentation.Validate(script));
        Assert.Equal(code, finding.Code); Assert.Equal("helper", finding.Function);
    }
}
