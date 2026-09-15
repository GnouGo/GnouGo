using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComputationScopeTests
{
    [Theory]
    [InlineData("root", "paths")]
    [InlineData("racine", "chemins")]
    public void CallbackShadowingDoesNotInvalidateActualUseOfTheOuterBinding(string parameter, string items)
    {
        var value = new PlanningValue { Kind = "compute", Text = $"const roots = {items}.map(p => p || {parameter}); return roots.map({parameter} => ({{path: {parameter}}}));",
            Members = [new(parameter, new() { Kind = "input", Source = parameter }), new(items, new() { Kind = "input", Source = items })] };
        PlanningComputations.Validate(value);
        value.Text = $"return {items}.map({parameter} => ({{path: {parameter}}}));";
        Assert.Contains("unused", Assert.Throws<InvalidOperationException>(() => PlanningComputations.Validate(value)).Message);
    }

    [Theory]
    [InlineData("const result = source; { const source = 'inner'; result.length; } return result;")]
    [InlineData("function local(source) { return source; } return local(source);")]
    [InlineData("const local = ({source}) => source; return local({source});")]
    [InlineData("try { throw 'inner'; } catch (source) { String(source); } return source;")]
    [InlineData("for (let source of ['inner']) { String(source); } return source;")]
    public void NestedBindingsRemainLocal(string body) => PlanningComputations.Validate(Value(body));

    [Theory]
    [InlineData("return ['constant'].map(source => source);")]
    [InlineData("function source() { return 'constant'; } return source();")]
    [InlineData("{ const source = 'inner'; return source; } return 'constant';")]
    [InlineData("for (var source of ['inner']) { String(source); } return source;")]
    [InlineData("const {source} = {source: 'inner'}; return source;")]
    public void InnerReferencesCannotEstablishUseOfAnOuterBinding(string body) => Assert.Contains("unused", Assert.Throws<InvalidOperationException>(() => PlanningComputations.Validate(Value(body))).Message);

    [Theory]
    [InlineData("{ const hidden = source; } return hidden;")]
    [InlineData("const f = hidden => hidden; return source + hidden;")]
    [InlineData("try { throw source; } catch (hidden) {} return hidden;")]
    public void OutOfScopeLocalsCannotMasqueradeAsDeclaredDependencies(string body) => Assert.Contains("Undeclared", Assert.Throws<InvalidOperationException>(() => PlanningComputations.Validate(Value(body))).Message);

    private static PlanningValue Value(string body) => new() { Kind = "compute", Text = body, Members = [new("source", new() { Kind = "input", Source = "source" })] };
}
