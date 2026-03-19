using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using Xunit;
namespace GnOuGo.Flow.Tests.Expressions;
public class ExpressionEvaluatorTests
{
    private readonly ExpressionEvaluator _evaluator = new();
    private static JsonObject MakeContext(JsonNode? inputs = null, JsonNode? steps = null) =>
        new() { ["inputs"] = inputs ?? new JsonObject(), ["steps"] = steps ?? new JsonObject(), ["env"] = new JsonObject() };
    [Fact]
    public void Evaluate_NumberLiteral_ReturnsNumber()
    {
        var result = _evaluator.Evaluate("42", null);
        Assert.Equal(42, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_StringLiteral_ReturnsString()
    {
        var result = _evaluator.Evaluate("\"hello\"", null);
        Assert.Equal("hello", result!.GetValue<string>());
    }
    [Fact]
    public void Evaluate_TrueLiteral_ReturnsBool()
    {
        var result = _evaluator.Evaluate("true", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_FalseLiteral_ReturnsBool()
    {
        var result = _evaluator.Evaluate("false", null);
        Assert.False(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_NullLiteral_ReturnsNull()
    {
        var result = _evaluator.Evaluate("null", null);
        Assert.Null(result);
    }
    [Fact]
    public void Evaluate_Addition_ReturnsSum()
    {
        var result = _evaluator.Evaluate("3 + 4", null);
        Assert.Equal(7, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_StringConcat_WithPlus()
    {
        var ctx = MakeContext(new JsonObject { ["a"] = "hello" });
        var result = _evaluator.Evaluate("data.inputs.a + \" world\"", ctx);
        Assert.Equal("hello world", result!.GetValue<string>());
    }
    [Fact]
    public void Evaluate_Subtraction_ReturnsDifference()
    {
        var result = _evaluator.Evaluate("10 - 3", null);
        Assert.Equal(7, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_Multiplication_ReturnsProduct()
    {
        var result = _evaluator.Evaluate("3 * 4", null);
        Assert.Equal(12, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_Division_ReturnsQuotient()
    {
        var result = _evaluator.Evaluate("10 / 4", null);
        Assert.Equal(2.5, result!.GetValue<double>());
    }
    [Fact]
    public void Evaluate_Equality_True()
    {
        var result = _evaluator.Evaluate("1 == 1", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_Equality_False()
    {
        var result = _evaluator.Evaluate("1 == 2", null);
        Assert.False(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_Inequality_True()
    {
        var result = _evaluator.Evaluate("1 != 2", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_NullEquality()
    {
        var result = _evaluator.Evaluate("null == null", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_NullInequality()
    {
        var result = _evaluator.Evaluate("null != 1", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LessThan()
    {
        Assert.True(_evaluator.Evaluate("1 < 2", null)!.GetValue<bool>());
        Assert.False(_evaluator.Evaluate("2 < 1", null)!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LessThanOrEqual()
    {
        Assert.True(_evaluator.Evaluate("1 <= 1", null)!.GetValue<bool>());
        Assert.True(_evaluator.Evaluate("1 <= 2", null)!.GetValue<bool>());
        Assert.False(_evaluator.Evaluate("2 <= 1", null)!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_GreaterThan()
    {
        Assert.True(_evaluator.Evaluate("2 > 1", null)!.GetValue<bool>());
        Assert.False(_evaluator.Evaluate("1 > 2", null)!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_GreaterThanOrEqual()
    {
        Assert.True(_evaluator.Evaluate("2 >= 2", null)!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LogicalAnd_BothTrue()
    {
        var result = _evaluator.Evaluate("true && true", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LogicalAnd_OneFalse()
    {
        var result = _evaluator.Evaluate("true && false", null);
        Assert.False(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LogicalOr_OneTrue()
    {
        var result = _evaluator.Evaluate("false || true", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_LogicalOr_BothFalse()
    {
        var result = _evaluator.Evaluate("false || false", null);
        Assert.False(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_UnaryNot_True()
    {
        var result = _evaluator.Evaluate("!false", null);
        Assert.True(result!.GetValue<bool>());
    }
    [Fact]
    public void Evaluate_UnaryNegate()
    {
        var result = _evaluator.Evaluate("-(5)", null);
        Assert.Equal(-5, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_DataInputsAccess()
    {
        var ctx = MakeContext(new JsonObject { ["name"] = "Alice" });
        var result = _evaluator.Evaluate("data.inputs.name", ctx);
        Assert.Equal("Alice", result!.GetValue<string>());
    }
    [Fact]
    public void Evaluate_DataStepsAccess()
    {
        var ctx = MakeContext(steps: new JsonObject { ["s1"] = new JsonObject { ["text"] = "ok" } });
        var result = _evaluator.Evaluate("data.steps.s1.text", ctx);
        Assert.Equal("ok", result!.GetValue<string>());
    }
    [Fact]
    public void Evaluate_OptionalChaining_NullObject()
    {
        var ctx = MakeContext();
        // In JS, optional chaining uses ?. which Jint supports
        var result = _evaluator.Evaluate("data.steps?.missing?.value", ctx);
        Assert.Null(result);
    }
    [Fact]
    public void Evaluate_IndexAccess_Array()
    {
        var ctx = MakeContext(new JsonObject { ["items"] = new JsonArray(JsonValue.Create(10), JsonValue.Create(20)) });
        var result = _evaluator.Evaluate("data.inputs.items[1]", ctx);
        Assert.Equal(20, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_IndexAccess_Object()
    {
        var ctx = MakeContext(new JsonObject { ["obj"] = new JsonObject { ["key"] = "val" } });
        var result = _evaluator.Evaluate("data.inputs.obj[\"key\"]", ctx);
        Assert.Equal("val", result!.GetValue<string>());
    }
    [Fact]
    public void Evaluate_IndexAccess_OutOfRange_ReturnsNull()
    {
        var ctx = MakeContext(new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1)) });
        var result = _evaluator.Evaluate("data.inputs.items[99]", ctx);
        Assert.Null(result);
    }
    [Fact]
    public void Evaluate_FunctionCall_Len()
    {
        var ctx = MakeContext(new JsonObject { ["text"] = "hello" });
        var result = _evaluator.Evaluate("len(data.inputs.text)", ctx);
        Assert.Equal(5, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_FunctionCall_LenArray()
    {
        var ctx = MakeContext(new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3)) });
        var result = _evaluator.Evaluate("len(data.inputs.items)", ctx);
        Assert.Equal(3, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_UnknownFunction_Throws()
    {
        Assert.Throws<WorkflowRuntimeException>(() =>
            _evaluator.Evaluate("unknownFn(1)", null));
    }
    [Fact]
    public void Evaluate_CustomFunction_RegisteredViaConstructor()
    {
        var extras = new Dictionary<string, Func<JsonNode?[], JsonNode?>>
        {
            ["double_val"] = args => JsonValue.Create(ExpressionEvaluator.GetNumber(args[0]) * 2)
        };
        var eval = new ExpressionEvaluator(extras);
        var result = eval.Evaluate("double_val(21)", null);
        Assert.Equal(42, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_ErrorContext_AccessesErrorAndStep()
    {
        var ctx = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = "LLM_TIMEOUT", ["message"] = "timed out" },
            ["step"] = new JsonObject { ["id"] = "s1", ["type"] = "llm.call" }
        };
        var code = _evaluator.Evaluate("error.code", ctx);
        Assert.Equal("LLM_TIMEOUT", code!.GetValue<string>());
        var stepId = _evaluator.Evaluate("step.id", ctx);
        Assert.Equal("s1", stepId!.GetValue<string>());
    }
    [Fact]
    public void GetNumber_FromNull_ReturnsZero()
    {
        Assert.Equal(0.0, ExpressionEvaluator.GetNumber(null));
    }
    [Fact]
    public void GetNumber_FromString_ParsesNumber()
    {
        Assert.Equal(3.14, ExpressionEvaluator.GetNumber(JsonValue.Create("3.14")));
    }
    [Fact]
    public void GetNumber_FromNonNumericString_Throws()
    {
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.GetNumber(JsonValue.Create("abc")));
    }
    [Fact]
    public void GetBool_FromNull_ReturnsFalse()
    {
        Assert.False(ExpressionEvaluator.GetBool(null));
    }
    [Fact]
    public void GetBool_FromNonBool_Throws()
    {
        Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.GetBool(JsonValue.Create(42)));
    }
    [Fact]
    public void GetString_FromNull_ReturnsEmptyString()
    {
        Assert.Equal("", ExpressionEvaluator.GetString(null));
    }
    [Fact]
    public void GetString_FromJsonObject_ReturnsJsonString()
    {
        var obj = new JsonObject { ["a"] = 1 };
        var result = ExpressionEvaluator.GetString(obj);
        Assert.Contains("\"a\"", result);
    }
    [Fact]
    public void ToJsonNode_AllTypes()
    {
        Assert.Null(ExpressionEvaluator.ToJsonNode(null));
        Assert.True(ExpressionEvaluator.ToJsonNode(true)!.GetValue<bool>());
        Assert.Equal(42.0, ExpressionEvaluator.ToJsonNode(42.0)!.GetValue<double>());
        Assert.Equal(7, ExpressionEvaluator.ToJsonNode(7)!.GetValue<int>());
        Assert.Equal("hi", ExpressionEvaluator.ToJsonNode("hi")!.GetValue<string>());
    }
    [Fact]
    public void Validate_ValidExpression_DoesNotThrow()
    {
        ExpressionEvaluator.Validate("data.inputs.x + 1");
    }
    [Fact]
    public void Validate_InvalidExpression_Throws()
    {
        Assert.Throws<ExpressionParseException>(() => ExpressionEvaluator.Validate("+++"));
    }
    [Fact]
    public void Evaluate_JsNativeFeatures_TernaryOperator()
    {
        var result = _evaluator.Evaluate("true ? 1 : 2", null);
        Assert.Equal(1, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_JsNativeFeatures_ArrayMethods()
    {
        var ctx = MakeContext(new JsonObject { ["items"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3)) });
        var result = _evaluator.Evaluate("data.inputs.items.length", ctx);
        Assert.Equal(3, result!.GetValue<int>());
    }
    [Fact]
    public void Evaluate_JsNativeFeatures_TemplateLiteral()
    {
        var ctx = MakeContext(new JsonObject { ["name"] = "World" });
        var result = _evaluator.Evaluate("`Hello ${data.inputs.name}!`", ctx);
        Assert.Equal("Hello World!", result!.GetValue<string>());
    }
}