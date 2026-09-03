from __future__ import annotations

from typing import Any

from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _invalid(message: str) -> WorkflowRuntimeException:
    return WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, message)


def _read_non_empty_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise _invalid(f"{label} must be a non-empty string")
    return value


def _require_only_fields(value: dict[str, Any], label: str, allowed: set[str]) -> None:
    unknown = next((field for field in value if field not in allowed), None)
    if unknown is not None:
        raise _invalid(f"{label} contains unknown field '{unknown}'")


def _is_safe_field_name(value: str) -> bool:
    return bool(value) and (value[0].isalpha() or value[0] == "_") and all(
        character.isalnum() or character == "_" for character in value
    )


class DecisionEvaluateExecutor:
    step_type = "decision.evaluate"
    step_description = "Atomically evaluate finite provider-neutral runtime decisions."
    dsl_snippet = """
### decision.evaluate - Evaluate finite runtime decisions atomically
```yaml
- id: compute_decisions
  type: decision.evaluate
  input:
    decisions:
      decision_1:
        allowed_values: [VALUE_A, VALUE_B, NO_EFFECT]
        cases:
          - when: "${data.steps.first.is_valid}"
            value: VALUE_A
          - when: "${data.steps.second.needs_attention}"
            value: VALUE_B
        default: NO_EFFECT
```
Every condition must resolve to a boolean and at most one case may match.
Output: `{ "decision_1": "VALUE_A" }`. Evaluation is atomic.
"""
    documented_exceptions = [
        (
            ErrorCodes.INPUT_VALIDATION,
            False,
            "The decision contract is malformed or exceeds the switch-case execution limit.",
        ),
        (
            ErrorCodes.DECISION_EVALUATION_UNRESOLVED,
            False,
            "A decision has overlapping matching cases or no matching case and no default.",
        ),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_value = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_value, dict):
            raise _invalid("decision.evaluate input must be an object")
        _require_only_fields(input_value, "decision.evaluate input", {"decisions"})

        decisions = input_value.get("decisions")
        if not isinstance(decisions, dict):
            raise _invalid("decision.evaluate requires a 'decisions' object")
        if not decisions:
            raise _invalid("decision.evaluate requires at least one decision")
        if len(decisions) > ctx.limits.max_switch_cases:
            raise _invalid(
                f"Decision count ({len(decisions)}) exceeds limit ({ctx.limits.max_switch_cases})"
            )

        selected: list[tuple[str, str]] = []
        for field, contract_value in decisions.items():
            if not isinstance(field, str) or not _is_safe_field_name(field):
                raise _invalid(f"Decision field '{field}' must be a safe identifier")
            if not isinstance(contract_value, dict):
                raise _invalid(f"Decision '{field}' must be an object")
            _require_only_fields(
                contract_value,
                f"Decision '{field}'",
                {"allowed_values", "cases", "default"},
            )

            allowed_values_value = contract_value.get("allowed_values")
            if not isinstance(allowed_values_value, list) or not allowed_values_value:
                raise _invalid(f"Decision '{field}' allowed_values must be a non-empty array")
            allowed_values = [
                _read_non_empty_string(value, f"Decision '{field}' allowed_values")
                for value in allowed_values_value
            ]
            if len(set(allowed_values)) != len(allowed_values):
                raise _invalid(f"Decision '{field}' allowed_values must contain unique strings")
            allowed = set(allowed_values)

            cases = contract_value.get("cases")
            if not isinstance(cases, list):
                raise _invalid(f"Decision '{field}' requires a 'cases' array")
            if not cases:
                raise _invalid(f"Decision '{field}' requires at least one case")
            if len(cases) > ctx.limits.max_switch_cases:
                raise _invalid(
                    f"Decision '{field}' case count ({len(cases)}) exceeds limit ({ctx.limits.max_switch_cases})"
                )

            case_values: set[str] = set()
            matched_value: str | None = None
            for decision_case in cases:
                if not isinstance(decision_case, dict):
                    raise _invalid(f"Decision '{field}' cases must be objects")
                _require_only_fields(
                    decision_case,
                    f"Decision '{field}' case",
                    {"when", "value"},
                )
                if "when" not in decision_case:
                    raise _invalid(f"Decision '{field}' case requires 'when'")
                matches = decision_case["when"]
                if type(matches) is not bool:
                    raise _invalid(f"Decision '{field}' case 'when' must resolve to a boolean")
                value = _read_non_empty_string(
                    decision_case.get("value"),
                    f"Decision '{field}' case value",
                )
                if value not in allowed:
                    raise _invalid(f"Decision '{field}' case value must be declared in allowed_values")
                if value in case_values:
                    raise _invalid(f"Decision '{field}' case values must be unique")
                case_values.add(value)

                if matches:
                    if matched_value is not None:
                        raise WorkflowRuntimeException(
                            ErrorCodes.DECISION_EVALUATION_UNRESOLVED,
                            f"Decision '{field}' has more than one matching case",
                        )
                    matched_value = value

            if "default" in contract_value:
                default_value = _read_non_empty_string(
                    contract_value["default"], f"Decision '{field}' default"
                )
                if default_value not in allowed:
                    raise _invalid(f"Decision '{field}' default must be declared in allowed_values")
                if matched_value is None:
                    matched_value = default_value

            if matched_value is None:
                raise WorkflowRuntimeException(
                    ErrorCodes.DECISION_EVALUATION_UNRESOLVED,
                    f"Decision '{field}' has no matching case and no default",
                )
            selected.append((field, matched_value))

        return dict(selected)
