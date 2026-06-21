using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class InputTypeValidatorTests
{
    // ── Base type checks ──

    [Fact]
    public void Validate_StringInput_ValidValue_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef { Type = "string" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = "hello" });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StringInput_NumberProvided_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "string" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = 42 });
        Assert.Single(errors);
        Assert.Contains("expected string", errors[0]);
    }

    [Fact]
    public void Validate_NumberInput_ValidValue_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef { Type = "number" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = 3.14 });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NumberInput_StringProvided_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "number" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = "not-a-number" });
        Assert.Single(errors);
        Assert.Contains("expected number", errors[0]);
    }

    [Fact]
    public void Validate_IntegerInput_ValidValue_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef { Type = "integer" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = 42 });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_IntegerInput_FractionalValue_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "integer" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = 4.2 });
        Assert.Single(errors);
        Assert.Contains("expected integer", errors[0]);
    }

    [Fact]
    public void Validate_BooleanInput_ValidValue_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef { Type = "boolean" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = true });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_BooleanInput_StringProvided_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "boolean" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = "yes" });
        Assert.Single(errors);
        Assert.Contains("expected boolean", errors[0]);
    }

    [Fact]
    public void Validate_RequiredInput_Missing_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "string", Required = true });
        var errors = InputTypeValidator.Validate(wf, new JsonObject());
        Assert.Single(errors);
        Assert.Contains("required", errors[0]);
    }

    [Fact]
    public void Validate_OptionalInput_Missing_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef { Type = "string", Required = false });
        var errors = InputTypeValidator.Validate(wf, new JsonObject());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AnyType_AcceptsAnything()
    {
        var wf = MakeWorkflow(new InputDef { Type = "any" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = new JsonArray(1, 2) });
        Assert.Empty(errors);
    }

    // ── Array with items ──

    [Fact]
    public void Validate_ArrayInput_ValidElementTypes_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "array",
            Items = new InputDef { Type = "string" }
        });
        var arr = new JsonArray("a", "b", "c");
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = arr });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ArrayInput_InvalidElement_Error()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "array",
            Items = new InputDef { Type = "string" }
        });
        var arr = new JsonArray("a", 42, "c");
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = arr });
        Assert.Single(errors);
        Assert.Contains("x[1]", errors[0]);
        Assert.Contains("expected string", errors[0]);
    }

    [Fact]
    public void Validate_ArrayInput_NotAnArray_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "array" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = "not-array" });
        Assert.Single(errors);
        Assert.Contains("expected array", errors[0]);
    }

    [Fact]
    public void Validate_ArrayInput_NoItems_AcceptsAnyElements()
    {
        var wf = MakeWorkflow(new InputDef { Type = "array" });
        var arr = new JsonArray("a", 1, true);
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = arr });
        Assert.Empty(errors);
    }

    // ── Object with properties ──

    [Fact]
    public void Validate_ObjectInput_ValidProperties_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "object",
            Properties = new Dictionary<string, InputDef>
            {
                ["name"] = new() { Type = "string" },
                ["age"] = new() { Type = "number" }
            }
        });
        var obj = new JsonObject { ["name"] = "Alice", ["age"] = 30 };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ObjectInput_WrongPropertyType_Error()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "object",
            Properties = new Dictionary<string, InputDef>
            {
                ["name"] = new() { Type = "string" },
                ["age"] = new() { Type = "number" }
            }
        });
        var obj = new JsonObject { ["name"] = "Alice", ["age"] = "thirty" };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Single(errors);
        Assert.Contains("x.age", errors[0]);
        Assert.Contains("expected number", errors[0]);
    }

    [Fact]
    public void Validate_ObjectInput_MissingRequiredProperty_Error()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "object",
            Properties = new Dictionary<string, InputDef>
            {
                ["host"] = new() { Type = "string" },
                ["port"] = new() { Type = "number" }
            },
            RequiredProperties = new List<string> { "host" }
        });
        var obj = new JsonObject { ["port"] = 8080 };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Single(errors);
        Assert.Contains("host", errors[0]);
    }

    [Fact]
    public void Validate_ObjectInput_NotAnObject_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "object" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = "not-object" });
        Assert.Single(errors);
        Assert.Contains("expected object", errors[0]);
    }

    // ── Dictionary ──

    [Fact]
    public void Validate_DictionaryInput_ValidValues_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "dictionary",
            AdditionalProperties = new InputDef { Type = "number" }
        });
        var obj = new JsonObject { ["math"] = 95, ["science"] = 87 };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_DictionaryInput_InvalidValue_Error()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "dictionary",
            AdditionalProperties = new InputDef { Type = "number" }
        });
        var obj = new JsonObject { ["math"] = 95, ["science"] = "excellent" };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Single(errors);
        Assert.Contains("science", errors[0]);
        Assert.Contains("expected number", errors[0]);
    }

    [Fact]
    public void Validate_DictionaryInput_NotAnObject_Error()
    {
        var wf = MakeWorkflow(new InputDef { Type = "dictionary" });
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = new JsonArray() });
        Assert.Single(errors);
        Assert.Contains("expected dictionary", errors[0]);
    }

    // ── Nested: array of objects ──

    [Fact]
    public void Validate_ArrayOfObjects_ValidData_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "array",
            Items = new InputDef
            {
                Type = "object",
                Properties = new Dictionary<string, InputDef>
                {
                    ["name"] = new() { Type = "string" },
                    ["score"] = new() { Type = "number" }
                }
            }
        });
        var arr = new JsonArray(
            new JsonObject { ["name"] = "Alice", ["score"] = 95 },
            new JsonObject { ["name"] = "Bob", ["score"] = 87 }
        );
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = arr });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ArrayOfObjects_NestedError()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "array",
            Items = new InputDef
            {
                Type = "object",
                Properties = new Dictionary<string, InputDef>
                {
                    ["name"] = new() { Type = "string" },
                    ["score"] = new() { Type = "number" }
                }
            }
        });
        var arr = new JsonArray(
            new JsonObject { ["name"] = "Alice", ["score"] = "not-a-number" }
        );
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = arr });
        Assert.Single(errors);
        Assert.Contains("x[0].score", errors[0]);
    }

    // ── Dictionary of complex values ──

    [Fact]
    public void Validate_DictionaryOfObjects_ValidData_NoErrors()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "dictionary",
            AdditionalProperties = new InputDef
            {
                Type = "object",
                Properties = new Dictionary<string, InputDef>
                {
                    ["url"] = new() { Type = "string" },
                    ["port"] = new() { Type = "number" }
                }
            }
        });
        var obj = new JsonObject
        {
            ["dev"] = new JsonObject { ["url"] = "http://localhost", ["port"] = 3000 },
            ["prod"] = new JsonObject { ["url"] = "https://prod.io", ["port"] = 443 }
        };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_DictionaryOfObjects_NestedError()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "dictionary",
            AdditionalProperties = new InputDef
            {
                Type = "object",
                Properties = new Dictionary<string, InputDef>
                {
                    ["url"] = new() { Type = "string" },
                    ["port"] = new() { Type = "number" }
                }
            }
        });
        var obj = new JsonObject
        {
            ["dev"] = new JsonObject { ["url"] = "http://localhost", ["port"] = "oops" }
        };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Single(errors);
        Assert.Contains("x['dev'].port", errors[0]);
    }

    // ── Object with additional_properties for extra keys ──

    [Fact]
    public void Validate_ObjectWithAdditionalProperties_ExtraKeysValidated()
    {
        var wf = MakeWorkflow(new InputDef
        {
            Type = "object",
            Properties = new Dictionary<string, InputDef>
            {
                ["name"] = new() { Type = "string" }
            },
            AdditionalProperties = new InputDef { Type = "number" }
        });
        var obj = new JsonObject { ["name"] = "test", ["extra1"] = 10, ["extra2"] = "bad" };
        var errors = InputTypeValidator.Validate(wf, new JsonObject { ["x"] = obj });
        Assert.Single(errors);
        Assert.Contains("x.extra2", errors[0]);
    }

    // ── Helper ──

    private static WorkflowDef MakeWorkflow(InputDef def)
    {
        return new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef> { ["x"] = def }
        };
    }
}
