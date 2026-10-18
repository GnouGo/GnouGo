using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class FlowTypeUnionTests
{
    [Fact]
    public void UnionPreservesStructuralEqualityAndFirstVariant()
    {
        var first = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
        {
            ["a"] = new(new() { Kind = FlowTypeKind.Union, Variants = [FlowTypeDescriptor.String, FlowTypeDescriptor.Null] }, true),
            ["b"] = new(FlowTypeDescriptor.Array(FlowTypeDescriptor.Integer))
        });
        var equivalent = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
        {
            ["b"] = new(FlowTypeDescriptor.Array(FlowTypeDescriptor.Integer)),
            ["a"] = new(new() { Kind = FlowTypeKind.Union, Variants = [FlowTypeDescriptor.Null, FlowTypeDescriptor.String] }, true)
        }) with { Description = "Ignored by the existing union identity", Minimum = 7 };
        Assert.Same(first, FlowTypeDescriptor.Union([first, equivalent]));
    }

    [Fact]
    public void UnionKeepsDifferentContractsAndVariantMultiplicity()
    {
        var required = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor> { ["x"] = new(FlowTypeDescriptor.String, true) });
        var optional = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor> { ["x"] = new(FlowTypeDescriptor.String) });
        var variants = new[]
        {
            required, optional, required with { AllowsAdditionalProperties = true },
            FlowTypeDescriptor.Dictionary(FlowTypeDescriptor.String),
            FlowTypeDescriptor.Array(FlowTypeDescriptor.String), FlowTypeDescriptor.Array(FlowTypeDescriptor.Number),
            FlowTypeDescriptor.Enum("a", "b"), FlowTypeDescriptor.Enum("b", "a"),
            FlowTypeDescriptor.Enum("a|b"),
            FlowTypeDescriptor.Array(new() { Kind = FlowTypeKind.Union, Variants = [FlowTypeDescriptor.String, FlowTypeDescriptor.Null] }),
            FlowTypeDescriptor.Array(new() { Kind = FlowTypeKind.Union, Variants = [FlowTypeDescriptor.String, FlowTypeDescriptor.String, FlowTypeDescriptor.Null] })
        };
        Assert.Equal(variants, FlowTypeDescriptor.Union(variants).Variants);
        var upper = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>(StringComparer.OrdinalIgnoreCase) { ["X"] = new(FlowTypeDescriptor.String, true) });
        Assert.Equal(2, FlowTypeDescriptor.Union([required, upper]).Variants.Count);
        var separated = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
        { ["a"] = new(FlowTypeDescriptor.String), ["b"] = new(FlowTypeDescriptor.Number) });
        var delimiterName = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
        { ["a:False:String;allowsAdditional:True,b"] = new(FlowTypeDescriptor.Number) });
        Assert.Equal(2, FlowTypeDescriptor.Union([separated, delimiterName]).Variants.Count);
    }

    [Fact]
    public void UnionComparesSharedDescendantsWithoutExpandingTheTree()
    {
        static FlowTypeDescriptor Shared(int depth)
        {
            var type = FlowTypeDescriptor.Number;
            for (var i = 0; i < depth; i++) type = FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
            { ["left"] = new(type, true), ["right"] = new(type, true) });
            return type;
        }
        var first = Shared(101);
        Assert.Same(first, FlowTypeDescriptor.Union([first, Shared(101)]));
    }

    [Fact]
    public void UnionDoesNotReuseHashesAcrossMutableContractsOrAcceptCycles()
    {
        var properties = new Dictionary<string, FlowPropertyDescriptor>();
        var mutable = FlowTypeDescriptor.Object(properties);
        var empty = FlowTypeDescriptor.Object();
        Assert.Same(mutable, FlowTypeDescriptor.Union([mutable, empty]));
        properties["new"] = new(FlowTypeDescriptor.String, true);
        Assert.Equal(2, FlowTypeDescriptor.Union([mutable, empty]).Variants.Count);
        properties["cycle"] = new(mutable);
        Assert.Throws<InvalidOperationException>(() => FlowTypeDescriptor.Union([mutable, empty]));
    }
}
