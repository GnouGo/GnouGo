from __future__ import annotations

from dataclasses import dataclass

from .errors import ErrorCodes
from .expressions import ExpressionEvaluator, StringInterpolator
from .models import (
    CompiledDocument,
    CompiledStep,
    CompiledSwitchCase,
    CompiledWorkflow,
    InputDef,
    OutputDef,
    StepDef,
    WorkflowDef,
    WorkflowDocument,
)
from .step_types import STEP_TYPES


@dataclass(slots=True)
class ValidationError:
    code: str
    message: str
    workflow_name: str | None = None
    step_id: str | None = None
    field: str | None = None


class WorkflowCompilationException(Exception):
    def __init__(self, errors: list[ValidationError]) -> None:
        super().__init__(
            f"Workflow compilation failed with {len(errors)} error(s):\n"
            + "\n".join(f"[{e.code}] {e.message}" for e in errors)
        )
        self.errors = errors


class WorkflowValidator:
    KNOWN_STEP_TYPES = set(STEP_TYPES)
    KNOWN_SCHEMA_TYPES = {"any", "string", "number", "boolean", "array", "object", "dictionary"}

    def validate(self, doc: WorkflowDocument) -> list[ValidationError]:
        errors: list[ValidationError] = []
        if doc.version != 1:
            errors.append(ValidationError(code="DSL_VERSION", message=f"Unsupported DSL version: {doc.version}"))
        if not doc.workflows:
            errors.append(ValidationError(code="NO_WORKFLOWS", message="Document must have at least one workflow"))
        if doc.entrypoint and doc.entrypoint not in doc.workflows:
            errors.append(ValidationError(code="INVALID_ENTRYPOINT", message=f"Entrypoint '{doc.entrypoint}' not found in workflows"))
        if doc.exports:
            for export_name in doc.exports:
                if export_name not in doc.workflows:
                    errors.append(ValidationError(code="INVALID_EXPORT", message=f"Exported workflow '{export_name}' not found"))

        for name, wf in doc.workflows.items():
            self._validate_workflow(name, wf, doc, errors)
        self._detect_cycles(doc, errors)
        return errors

    def _validate_workflow(
        self,
        name: str,
        wf: WorkflowDef,
        doc: WorkflowDocument,
        errors: list[ValidationError],
    ) -> None:
        if not wf.steps:
            errors.append(ValidationError(code="EMPTY_STEPS", message="Workflow has no steps", workflow_name=name))

        seen_ids: set[str] = set()
        self._collect_ids(wf.steps, seen_ids, name, errors)
        for step in wf.steps:
            self._validate_step(step, name, doc, errors)

        if wf.inputs:
            for key, definition in wf.inputs.items():
                self._validate_input_def(definition, name, key, errors)

        if wf.outputs:
            for key, out in wf.outputs.items():
                if out.expr:
                    self._validate_expr(out.expr, name, None, f"outputs.{key}", errors)
                self._validate_output_def(out, name, key, errors)

    def _collect_ids(self, steps: list[StepDef], seen: set[str], wf_name: str, errors: list[ValidationError]) -> None:
        for step in steps:
            if step.id in seen:
                errors.append(
                    ValidationError(
                        code="DUPLICATE_STEP_ID",
                        workflow_name=wf_name,
                        step_id=step.id,
                        message=f"Duplicate step ID: '{step.id}'",
                    )
                )
            seen.add(step.id)
            for nested in self._nested_steps(step):
                self._collect_ids(nested, seen, wf_name, errors)

    def _nested_steps(self, step: StepDef) -> list[list[StepDef]]:
        nested: list[list[StepDef]] = []
        if step.steps:
            nested.append(step.steps)
        if step.branches:
            nested.extend(branch.steps for branch in step.branches)
        if step.cases:
            nested.extend(case.steps for case in step.cases)
        if step.default:
            nested.append(step.default)
        return nested

    def _validate_step(
        self,
        step: StepDef,
        wf_name: str,
        doc: WorkflowDocument,
        errors: list[ValidationError],
    ) -> None:
        if step.type not in self.KNOWN_STEP_TYPES:
            errors.append(
                ValidationError(
                    code=ErrorCodes.STEP_TYPE_UNKNOWN,
                    workflow_name=wf_name,
                    step_id=step.id,
                    message=f"Unknown step type: '{step.type}'",
                )
            )

        if step.if_:
            self._validate_expr(step.if_, wf_name, step.id, "if", errors)
        if step.expr:
            self._validate_expr(step.expr, wf_name, step.id, "expr", errors)

        if step.type in {"sequence", "loop.sequential", "loop.parallel"} and not step.steps:
            errors.append(ValidationError(code="MISSING_STEPS", workflow_name=wf_name, step_id=step.id, message=f"{step.type} requires 'steps'"))
        if step.type == "parallel" and not step.branches:
            errors.append(ValidationError(code="MISSING_BRANCHES", workflow_name=wf_name, step_id=step.id, message="parallel requires 'branches'"))
        if step.type == "switch" and not step.cases:
            errors.append(ValidationError(code="MISSING_CASES", workflow_name=wf_name, step_id=step.id, message="switch requires 'cases'"))

        if step.type == "workflow.call" and isinstance(step.input, dict):
            ref = step.input.get("ref")
            if isinstance(ref, dict) and ref.get("kind") == "local":
                name = ref.get("name")
                if name and name not in doc.workflows:
                    errors.append(
                        ValidationError(
                            code="INVALID_WORKFLOW_REF",
                            workflow_name=wf_name,
                            step_id=step.id,
                            message=f"Local workflow '{name}' not found",
                        )
                    )

        if step.cases:
            for case in step.cases:
                if case.when is not None:
                    self._validate_expr(case.when, wf_name, step.id, "cases.when", errors)

        for nested in self._nested_steps(step):
            for child in nested:
                self._validate_step(child, wf_name, doc, errors)

    def _validate_expr(
        self,
        expr: str,
        wf_name: str,
        step_id: str | None,
        field: str,
        errors: list[ValidationError],
    ) -> None:
        if not StringInterpolator.has_expressions(expr):
            return
        from .expressions import _scan_expressions
        for _start, _end, part in _scan_expressions(expr):
            try:
                ExpressionEvaluator.validate(part.strip())
            except Exception as exc:
                errors.append(
                    ValidationError(
                        code=ErrorCodes.EXPR_PARSE,
                        workflow_name=wf_name,
                        step_id=step_id,
                        field=field,
                        message=f"Invalid expression: {exc}",
                    )
                )

    def _detect_cycles(self, doc: WorkflowDocument, errors: list[ValidationError]) -> None:
        graph: dict[str, set[str]] = {name: set() for name in doc.workflows}
        for wf_name, wf in doc.workflows.items():
            self._collect_local_calls(wf.steps, graph[wf_name])

        visited: set[str] = set()
        stack: set[str] = set()

        def dfs(node: str) -> bool:
            if node in stack:
                return True
            if node in visited:
                return False
            visited.add(node)
            stack.add(node)
            for nxt in graph.get(node, set()):
                if dfs(nxt):
                    return True
            stack.remove(node)
            return False

        for name in graph:
            if dfs(name):
                errors.append(ValidationError(code=ErrorCodes.WORKFLOW_CYCLE_DETECTED, message=f"Cycle detected involving workflow '{name}'"))

    def _collect_local_calls(self, steps: list[StepDef], called: set[str]) -> None:
        for step in steps:
            if step.type == "workflow.call" and isinstance(step.input, dict):
                ref = step.input.get("ref")
                if isinstance(ref, dict) and ref.get("kind") == "local" and ref.get("name"):
                    called.add(str(ref["name"]))
            for nested in self._nested_steps(step):
                self._collect_local_calls(nested, called)

    def _validate_input_def(
        self,
        definition: InputDef,
        wf_name: str,
        path: str,
        errors: list[ValidationError],
    ) -> None:
        type_lc = definition.type.lower()
        if type_lc not in self.KNOWN_SCHEMA_TYPES:
            errors.append(
                ValidationError(
                    code="INVALID_INPUT_TYPE",
                    workflow_name=wf_name,
                    message=f"Input '{path}': unknown type '{definition.type}'. Known types: {', '.join(sorted(self.KNOWN_SCHEMA_TYPES))}",
                )
            )

        if definition.items is not None and type_lc != "array":
            errors.append(
                ValidationError(
                    code="INVALID_INPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Input '{path}': 'items' is only valid when type is 'array', got '{definition.type}'",
                )
            )
        if definition.properties is not None and type_lc != "object":
            errors.append(
                ValidationError(
                    code="INVALID_INPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Input '{path}': 'properties' is only valid when type is 'object', got '{definition.type}'",
                )
            )
        if definition.required_properties is not None and type_lc != "object":
            errors.append(
                ValidationError(
                    code="INVALID_INPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Input '{path}': 'required' is only valid when type is 'object', got '{definition.type}'",
                )
            )
        if definition.additional_properties is not None and type_lc not in {"object", "dictionary"}:
            errors.append(
                ValidationError(
                    code="INVALID_INPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Input '{path}': 'additional_properties' is only valid when type is 'object' or 'dictionary', got '{definition.type}'",
                )
            )

        if definition.required_properties is not None and definition.properties is not None:
            for prop_name in definition.required_properties:
                if prop_name not in definition.properties:
                    errors.append(
                        ValidationError(
                            code="INVALID_INPUT_SCHEMA",
                            workflow_name=wf_name,
                            message=f"Input '{path}': required property '{prop_name}' is not declared in 'properties'",
                        )
                    )

        if definition.items is not None:
            self._validate_input_def(definition.items, wf_name, f"{path}.items", errors)
        if definition.properties is not None:
            for prop_name, prop_def in definition.properties.items():
                self._validate_input_def(prop_def, wf_name, f"{path}.properties.{prop_name}", errors)
        if definition.additional_properties is not None:
            self._validate_input_def(definition.additional_properties, wf_name, f"{path}.additional_properties", errors)

    def _validate_output_def(
        self,
        definition: OutputDef,
        wf_name: str,
        path: str,
        errors: list[ValidationError],
    ) -> None:
        type_lc = definition.type.lower()
        if type_lc not in self.KNOWN_SCHEMA_TYPES:
            errors.append(
                ValidationError(
                    code="INVALID_OUTPUT_TYPE",
                    workflow_name=wf_name,
                    message=f"Output '{path}': unknown type '{definition.type}'. Known types: {', '.join(sorted(self.KNOWN_SCHEMA_TYPES))}",
                )
            )

        if definition.items is not None and type_lc != "array":
            errors.append(
                ValidationError(
                    code="INVALID_OUTPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Output '{path}': 'items' is only valid when type is 'array', got '{definition.type}'",
                )
            )
        if definition.properties is not None and type_lc != "object":
            errors.append(
                ValidationError(
                    code="INVALID_OUTPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Output '{path}': 'properties' is only valid when type is 'object', got '{definition.type}'",
                )
            )
        if definition.required_properties is not None and type_lc != "object":
            errors.append(
                ValidationError(
                    code="INVALID_OUTPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Output '{path}': 'required' is only valid when type is 'object', got '{definition.type}'",
                )
            )
        if definition.additional_properties is not None and type_lc not in {"object", "dictionary"}:
            errors.append(
                ValidationError(
                    code="INVALID_OUTPUT_SCHEMA",
                    workflow_name=wf_name,
                    message=f"Output '{path}': 'additional_properties' is only valid when type is 'object' or 'dictionary', got '{definition.type}'",
                )
            )

        if definition.required_properties is not None and definition.properties is not None:
            for prop_name in definition.required_properties:
                if prop_name not in definition.properties:
                    errors.append(
                        ValidationError(
                            code="INVALID_OUTPUT_SCHEMA",
                            workflow_name=wf_name,
                            message=f"Output '{path}': required property '{prop_name}' is not declared in 'properties'",
                        )
                    )

        if definition.properties is not None:
            for prop_name, prop_def in definition.properties.items():
                if prop_def.expr:
                    self._validate_expr(prop_def.expr, wf_name, None, f"outputs.{path}.{prop_name}", errors)
                self._validate_output_def(prop_def, wf_name, f"{path}.properties.{prop_name}", errors)
        if definition.items is not None:
            self._validate_output_def(definition.items, wf_name, f"{path}.items", errors)
        if definition.additional_properties is not None:
            self._validate_output_def(definition.additional_properties, wf_name, f"{path}.additional_properties", errors)


class WorkflowCompiler:
    def __init__(self) -> None:
        self._validator = WorkflowValidator()

    def validate(self, doc: WorkflowDocument) -> list[ValidationError]:
        return self._validator.validate(doc)

    def compile(self, doc: WorkflowDocument) -> CompiledDocument:
        errors = self._validator.validate(doc)
        hard_fail_codes = {ErrorCodes.EXPR_PARSE, "DSL_VERSION", "NO_WORKFLOWS", ErrorCodes.WORKFLOW_CYCLE_DETECTED, "INVALID_ENTRYPOINT"}
        if any(err.code in hard_fail_codes for err in errors):
            raise WorkflowCompilationException(errors)

        compiled = CompiledDocument(
            source=doc,
            entrypoint=doc.entrypoint or ("main" if "main" in doc.workflows else next(iter(doc.workflows), None)),
        )

        for name, wf in doc.workflows.items():
            compiled_wf = CompiledWorkflow(
                name=name,
                source=wf,
                steps=self._compile_steps(wf.steps),
                outputs=wf.outputs,
            )
            compiled.workflows[name] = compiled_wf

        for wf in compiled.workflows.values():
            wf.document = compiled

        return compiled

    def _compile_steps(self, steps: list[StepDef]) -> list[CompiledStep]:
        return [self._compile_step(step) for step in steps]

    def _compile_step(self, step: StepDef) -> CompiledStep:
        compiled = CompiledStep(source=step)
        if step.steps:
            compiled.steps = self._compile_steps(step.steps)
        if step.branches:
            compiled.branches = [self._compile_steps(b.steps) for b in step.branches]
        if step.cases:
            compiled.cases = [CompiledSwitchCase(source=c, steps=self._compile_steps(c.steps)) for c in step.cases]
        if step.default:
            compiled.default = self._compile_steps(step.default)
        return compiled

