from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path
from typing import Any

from .compilation import WorkflowCompilationException, WorkflowCompiler
from .errors import WorkflowParseException
from .models import InputDef, OutputDef, StepDef, StepStatus
from .parsing import WorkflowParser
from .runtime import WorkflowEngine, apply_workflow_input_defaults


def _parse_json_value(value: str) -> Any:
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def _load_input_json(value: str | None) -> dict[str, Any]:
    if not value:
        return {}
    raw = Path(value[1:]).read_text(encoding="utf-8") if value.startswith("@") else value
    parsed = json.loads(raw)
    if not isinstance(parsed, dict):
        raise ValueError("--input-json/-j must be a JSON object")
    return parsed


def _merge_inputs(input_json: str | None, input_pairs: list[str] | None) -> dict[str, Any]:
    inputs = dict(_load_input_json(input_json))
    for item in input_pairs or []:
        if "=" not in item or item.startswith("="):
            raise ValueError(f"Input must be key=value: {item}")
        key, value = item.split("=", 1)
        if not key:
            raise ValueError(f"Input key cannot be empty: {item}")
        inputs[key] = _parse_json_value(value)
    return inputs


def _format_input_type(definition: InputDef) -> str:
    typ = definition.type.lower()
    if typ == "array" and definition.items is not None:
        return f"array<{_format_input_type(definition.items)}>"
    if typ == "dictionary" and definition.additional_properties is not None:
        return f"dictionary<string, {_format_input_type(definition.additional_properties)}>"
    if typ == "object" and definition.properties is not None:
        props = ", ".join(f"{key}: {_format_input_type(value)}" for key, value in definition.properties.items())
        return f"object{{{props}}}"
    return definition.type


def _format_output_type(definition: OutputDef) -> str:
    typ = definition.type.lower()
    if typ == "array" and definition.items is not None:
        return f"array<{_format_output_type(definition.items)}>"
    if typ == "dictionary" and definition.additional_properties is not None:
        return f"dictionary<string, {_format_output_type(definition.additional_properties)}>"
    if typ == "object" and definition.properties is not None:
        props = ", ".join(f"{key}: {_format_output_type(value)}" for key, value in definition.properties.items())
        return f"object{{{props}}}"
    return definition.type


def _print_steps(steps: list[StepDef], indent: str = "") -> None:
    for step in steps:
        suffix = f"  if: {step.if_}" if step.if_ is not None else ""
        print(f"{indent}  [{step.type}] {step.id}{suffix}")
        if step.steps:
            _print_steps(step.steps, indent + "  ")
        if step.branches:
            for index, branch in enumerate(step.branches):
                print(f"{indent}    Branch {index}:")
                _print_steps(branch.steps, indent + "    ")
        if step.cases:
            for case in step.cases:
                print(f"{indent}    Case: {case.value or case.when or 'default'}")
                _print_steps(case.steps, indent + "    ")
        if step.default:
            print(f"{indent}    Default:")
            _print_steps(step.default, indent + "    ")


def _print_input_def(name: str, definition: InputDef, indent: str) -> None:
    req = "required" if definition.required else "optional"
    desc = f" — {definition.description}" if definition.description else ""
    default = f", default: {definition.default}" if definition.default is not None else ""
    print(f"{indent}{name}: {_format_input_type(definition)} ({req}{default}){desc}")


def _print_output_def(name: str, definition: OutputDef, indent: str) -> None:
    desc = f" — {definition.description}" if definition.description else ""
    expr = f" = {definition.expr}" if definition.expr else ""
    print(f"{indent}{name}: {_format_output_type(definition)}{expr}{desc}")


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="gnougo-flow", description="GnOuGo.Flow — YAML Workflow DSL Engine")
    subcommands = parser.add_subparsers(dest="command", required=True)

    validate = subcommands.add_parser("validate", help="Validate a workflow YAML file")
    validate.add_argument("yaml_path", type=Path, help="Path to workflow YAML file")

    inspect = subcommands.add_parser("inspect", help="Inspect a workflow YAML file structure")
    inspect.add_argument("yaml_path", type=Path, help="Path to workflow YAML file")

    run = subcommands.add_parser("run", help="Run a workflow YAML file")
    run.add_argument("yaml_path", type=Path, help="Path to workflow YAML file")
    run.add_argument("--workflow", default=None, help="Workflow name (default: entrypoint/main)")
    run.add_argument("--input", "-i", action="append", default=[], help="Input value as key=value; repeatable")
    run.add_argument("--input-json", "-j", default=None, help="Full JSON object input, or @path.json")
    return parser


def _read_yaml(path: Path) -> str:
    if not path.exists():
        raise FileNotFoundError(f"File not found: {path}")
    return path.read_text(encoding="utf-8")


async def _validate(args: argparse.Namespace) -> int:
    yaml_text = _read_yaml(args.yaml_path)
    try:
        doc = WorkflowParser.parse(yaml_text)
        errors = WorkflowCompiler().validate(doc)
    except WorkflowParseException as exc:
        print(f"Parse error: {exc}")
        return 1

    if not errors:
        print("✓ Validation passed — no errors.")
        return 0

    print(f"✗ {len(errors)} validation error(s):")
    for error in errors:
        location = ""
        if error.workflow_name:
            location += f" workflow={error.workflow_name}"
        if error.step_id:
            location += f" step={error.step_id}"
        if error.field:
            location += f" field={error.field}"
        print(f"  [{error.code}]{location} {error.message}")
    return 1


async def _inspect(args: argparse.Namespace) -> int:
    yaml_text = _read_yaml(args.yaml_path)
    try:
        doc = WorkflowParser.parse(yaml_text)
    except WorkflowParseException as exc:
        print(f"Parse error: {exc}")
        return 1

    print(f"Version: {doc.version}")
    print(f"Name: {doc.name or '(none)'}")
    print(f"Entrypoint: {doc.entrypoint or '(auto)'}")
    print(f"Workflows: {len(doc.workflows)}")
    print(f"Exports: {', '.join(doc.exports) if doc.exports else '(none)'}")
    for name, workflow in doc.workflows.items():
        print()
        print(f"  Workflow: {name}")
        if workflow.inputs:
            print("    Inputs:")
            for input_name, input_def in workflow.inputs.items():
                _print_input_def(input_name, input_def, "      ")
        print(f"    Steps: {len(workflow.steps)}")
        _print_steps(workflow.steps, "    ")
        if workflow.outputs:
            print("    Outputs:")
            for output_name, output_def in workflow.outputs.items():
                _print_output_def(output_name, output_def, "      ")
    return 0


async def _run(args: argparse.Namespace) -> int:
    yaml_text = _read_yaml(args.yaml_path)
    try:
        doc = WorkflowParser.parse(yaml_text)
        compiled = WorkflowCompiler().compile(doc)
    except WorkflowParseException as exc:
        print(f"Parse error: {exc}")
        return 1
    except WorkflowCompilationException as exc:
        print(f"Compilation error: {exc}")
        return 1

    target = args.workflow or compiled.entrypoint
    if not target or target not in compiled.workflows:
        print("No entrypoint workflow found. Define 'main' or set 'entrypoint'.")
        return 1

    try:
        inputs = _merge_inputs(args.input_json, args.input)
    except Exception as exc:
        print(f"Input error: {exc}")
        return 1

    workflow = compiled.workflows[target]
    inputs = apply_workflow_input_defaults(workflow.source, inputs)
    engine = WorkflowEngine()

    print(f"▶ Running workflow '{target}'...")
    result = await engine.execute_async(workflow, inputs)
    print()
    if result.success:
        print("✓ Workflow completed successfully.")
    else:
        print(f"✗ Workflow failed: {result.error.message if result.error else '(unknown error)'}")

    print()
    print("Steps executed:")
    for step in result.step_results:
        icon = "✓" if step.status == StepStatus.SUCCEEDED else "○" if step.status == StepStatus.SKIPPED else "✗" if step.status == StepStatus.FAILED else "?"
        print(f"  {icon} {step.step_id} ({step.step_type}) — {step.status.value} [{step.duration * 1000.0:.1f}ms]")

    if result.outputs is not None:
        print()
        print("Outputs:")
        print(json.dumps(result.outputs, indent=2, ensure_ascii=False, default=str))
    return 0 if result.success else 1


async def _dispatch(args: argparse.Namespace) -> int:
    try:
        if args.command == "validate":
            return await _validate(args)
        if args.command == "inspect":
            return await _inspect(args)
        if args.command == "run":
            return await _run(args)
    except FileNotFoundError as exc:
        print(str(exc))
        return 1
    raise ValueError(f"Unknown command: {args.command}")


def main(argv: list[str] | None = None) -> None:
    parser = _build_parser()
    args = parser.parse_args(argv)
    raise SystemExit(asyncio.run(_dispatch(args)))


if __name__ == "__main__":
    main()

