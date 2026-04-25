from gnougo_flow_core.expressions import ExpressionEvaluator, StringInterpolator


def test_expression_evaluator_and_string_interpolator() -> None:
    evaluator = ExpressionEvaluator()
    data = {"inputs": {"name": "World", "items": [1, 2, 3]}, "steps": {}, "env": {}}

    assert evaluator.evaluate("len(data.inputs.items)", data) == 3
    assert evaluator.evaluate("contains(lower(data.inputs.name), 'wor')", data) is True

    interpolator = StringInterpolator(evaluator)
    assert interpolator.interpolate("Hello ${data.inputs.name}", data) == "Hello World"
    assert interpolator.interpolate("${len(data.inputs.items)}", data) == 3


def test_expression_list_content_compatibility() -> None:
    evaluator = ExpressionEvaluator()
    data = {
        "inputs": {},
        "steps": {"tool": {"response": [{"type": "text", "text": "hello"}]}},
        "env": {},
    }

    assert evaluator.evaluate("len(data.steps.tool.response.content)", data) == 1
    assert evaluator.evaluate("data.steps.tool.response.content[0].text", data) == "hello"


def test_expression_to_json_alias_is_supported() -> None:
    evaluator = ExpressionEvaluator()
    data = {"inputs": {}, "steps": {}, "env": {}}

    expected = evaluator.evaluate("json({'ok': true, 'n': 2})", data)
    actual = evaluator.evaluate("toJson({'ok': true, 'n': 2})", data)
    assert actual == expected


