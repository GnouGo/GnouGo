from __future__ import annotations

import asyncio
import copy
import json
from dataclasses import asdict
from pathlib import Path
from typing import Annotated, Any

import typer
import yaml
from gnougo_flow_core import WorkflowCompiler, WorkflowEngine, WorkflowParser
from gnougo_flow_core.checkpointing import InMemoryWorkflowCheckpointer
from gnougo_flow_core.models import StepStatus
from gnougo_flow_core.runtime import apply_workflow_input_defaults
from rich.console import Console
from rich.table import Table

from .mcp_real import RealMcpFactory
from .openai_client import OpenAiLlmClient
from .settings import load_settings
from .stubs import AutoApproveHumanProvider, DemoMcpFactory, EchoLLMClient
from .telemetry import OTelWorkflowTelemetry, TelemetryConfig, setup_tracing

app = typer.Typer(help="GnOuGo Flow CLI demonstration runner")
examples_app = typer.Typer(help="Examples management")
app.add_typer(examples_app, name="examples")
console = Console()


def _load_example(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _resolve_telemetry_config(settings, otlp_endpoint_override: str | None) -> TelemetryConfig:
    endpoint = otlp_endpoint_override
    if endpoint is None and settings.telemetry.enabled:
        endpoint = settings.telemetry.otlp_endpoint

    return TelemetryConfig(
        service_name=settings.telemetry.service_name,
        otlp_endpoint=endpoint,
    )


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
        raise typer.BadParameter("--input-json/-j must be a JSON object")
    return parsed


def _merge_inputs(
    legacy_inputs: str | None,
    input_json: str | None,
    input_pairs: list[str] | None,
) -> dict[str, Any]:
    inputs: dict[str, Any] = {}
    if legacy_inputs:
        parsed_legacy = json.loads(legacy_inputs)
        if not isinstance(parsed_legacy, dict):
            raise typer.BadParameter("--inputs must be a JSON object")
        inputs.update(parsed_legacy)
    inputs.update(_load_input_json(input_json))
    for item in input_pairs or []:
        if "=" not in item or item.startswith("="):
            raise typer.BadParameter(f"Input must be key=value: {item}")
        key, value = item.split("=", 1)
        if not key:
            raise typer.BadParameter(f"Input key cannot be empty: {item}")
        inputs[key] = _parse_json_value(value)
    return inputs


def _workflow_payload(result) -> dict[str, Any]:
    return {
        "success": result.success,
        "outputs": result.outputs,
        "error": asdict(result.error) if result.error else None,
        "steps": [step.model_dump() for step in result.step_results],
    }


def _print_run_summary(target: str, result, *, json_output: bool) -> None:
    if json_output:
        console.print_json(data=_workflow_payload(result))
        return

    console.print(f"[cyan]▶ Running workflow '{target}'...[/cyan]")
    console.print()
    if result.success:
        console.print("[green]✓ Workflow completed successfully.[/green]")
    else:
        message = result.error.message if result.error else "(unknown error)"
        console.print(f"[red]✗ Workflow failed: {message}[/red]")

    console.print()
    console.print("Steps executed:")
    for step in result.step_results:
        icon = (
            "✓"
            if step.status == StepStatus.SUCCEEDED
            else "○"
            if step.status == StepStatus.SKIPPED
            else "✗"
            if step.status == StepStatus.FAILED
            else "?"
        )
        console.print(
            f"  {icon} {step.step_id} ({step.step_type}) — {step.status.value} "
            f"[{step.duration * 1000.0:.1f}ms]"
        )

    if result.outputs is not None:
        console.print()
        console.print("Outputs:")
        console.print(json.dumps(result.outputs, indent=2, ensure_ascii=False, default=str))


async def _run_async(
    workflow_file: Path,
    workflow_name: str | None,
    inputs: dict,
    otlp_endpoint: str | None,
    settings_file: Path | None,
    llm_mode: str,
    mcp_mode: str,
    run_id: str | None,
    json_output: bool,
) -> int:
    settings = load_settings(settings_file)
    setup_tracing(_resolve_telemetry_config(settings, otlp_endpoint))
    yaml_text = workflow_file.read_text(encoding="utf-8")
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)

    target = workflow_name or compiled.entrypoint
    if not target or target not in compiled.workflows:
        raise typer.BadParameter(f"Workflow '{target}' not found")

    engine = WorkflowEngine()
    if llm_mode == "stub":
        engine.llm_client = EchoLLMClient()
    else:
        openai_key = settings.openai.api_key
        if llm_mode == "openai" and not openai_key:
            raise typer.BadParameter(
                "OpenAI mode requires an API key. Set GNOUGO__OPENAI__API_KEY "
                "or settings.openai.api_key."
            )
        if openai_key:
            engine.llm_client = OpenAiLlmClient(settings.openai)
        else:
            engine.llm_client = EchoLLMClient()

    real_factory = RealMcpFactory(settings.mcp_servers)
    used_real_factory = False
    if mcp_mode == "stub":
        engine.mcp_client_factory = DemoMcpFactory()
    elif mcp_mode == "real":
        if not real_factory.has_servers():
            raise typer.BadParameter(
                "--mcp real requires configured stdio MCP servers in settings/appsettings"
            )
        engine.mcp_client_factory = real_factory
        used_real_factory = True
    else:
        if real_factory.has_servers():
            engine.mcp_client_factory = real_factory
            used_real_factory = True
        else:
            engine.mcp_client_factory = DemoMcpFactory()

    engine.human_input_provider = AutoApproveHumanProvider()
    engine.telemetry = OTelWorkflowTelemetry()
    if run_id:
        engine.limits.run_id = run_id
        engine.checkpointer = InMemoryWorkflowCheckpointer()

    workflow = compiled.workflows[target]
    merged_inputs = apply_workflow_input_defaults(workflow.source, copy.deepcopy(inputs))
    try:
        result = await engine.execute_async(workflow, merged_inputs)
    finally:
        if used_real_factory:
            await real_factory.aclose()

    _print_run_summary(target, result, json_output=json_output)
    if run_id and engine.checkpointer is not None:
        checkpoint = await engine.checkpointer.load_async(run_id)
        if checkpoint is not None and not json_output:
            console.print(
                f"\nCheckpoint: run_id={checkpoint.run_id}, "
                f"next_step_index={checkpoint.next_step_index}, status={checkpoint.status}"
            )
    return 0 if result.success else 1


@app.command("run")
def run_workflow(
    workflow_file: Annotated[Path, typer.Argument(help="Path to YAML workflow")],
    workflow: Annotated[str | None, typer.Option(help="Workflow name")] = None,
    inputs: Annotated[
        str | None,
        typer.Option("--inputs", help="Legacy JSON object for inputs; prefer -j/--input-json"),
    ] = None,
    input_pairs: Annotated[
        list[str] | None,
        typer.Option("--input", "-i", help="Input as key=value; repeatable"),
    ] = None,
    input_json: Annotated[
        str | None,
        typer.Option("--input-json", "-j", help="Full JSON object input, or @path.json"),
    ] = None,
    otlp_endpoint: Annotated[str | None, typer.Option(help="OTLP HTTP endpoint")] = None,
    settings_file: Annotated[
        Path | None,
        typer.Option("--settings", help="Path to settings.json|yaml"),
    ] = None,
    llm: Annotated[str, typer.Option("--llm", help="LLM backend: auto | openai | stub")] = "auto",
    mcp: Annotated[str, typer.Option("--mcp", help="MCP backend: auto | real | stub")] = "auto",
    run_id: Annotated[
        str | None,
        typer.Option("--run-id", help="Enable in-memory checkpoint saves for this run id"),
    ] = None,
    json_output: Annotated[
        bool,
        typer.Option("--json", help="Emit machine-readable JSON payload"),
    ] = False,
) -> None:
    llm_mode = llm.lower().strip()
    if llm_mode not in {"auto", "openai", "stub"}:
        raise typer.BadParameter("--llm must be one of: auto, openai, stub")
    mcp_mode = mcp.lower().strip()
    if mcp_mode not in {"auto", "real", "stub"}:
        raise typer.BadParameter("--mcp must be one of: auto, real, stub")
    parsed_inputs = _merge_inputs(inputs, input_json, input_pairs)
    code = asyncio.run(
        _run_async(
            workflow_file,
            workflow,
            parsed_inputs,
            otlp_endpoint,
            settings_file,
            llm_mode,
            mcp_mode,
            run_id,
            json_output,
        )
    )
    raise typer.Exit(code=code)


@app.command("validate")
def validate_workflow(
    workflow_file: Annotated[Path, typer.Argument(help="Path to YAML workflow")],
) -> None:
    yaml_text = workflow_file.read_text(encoding="utf-8")
    doc = WorkflowParser.parse(yaml_text)
    errors = WorkflowCompiler().validate(doc)
    if errors:
        table = Table(title="Validation errors")
        table.add_column("Code")
        table.add_column("Workflow")
        table.add_column("Step")
        table.add_column("Message")
        for err in errors:
            table.add_row(err.code, err.workflow_name or "", err.step_id or "", err.message)
        console.print(table)
        raise typer.Exit(code=1)
    console.print("[green]Validation OK[/green]")


@app.command("inspect")
def inspect_workflow(
    workflow_file: Annotated[Path, typer.Argument(help="Path to YAML workflow")],
) -> None:
    doc = WorkflowParser.parse(workflow_file.read_text(encoding="utf-8"))
    table = Table(title=f"Document: {doc.name or workflow_file.name}")
    table.add_column("Workflow")
    table.add_column("Steps")
    table.add_column("Has Outputs")
    for name, wf in doc.workflows.items():
        table.add_row(name, str(len(wf.steps)), "yes" if wf.outputs else "no")
    console.print(table)


@examples_app.command("list")
def list_examples() -> None:
    base = Path(__file__).resolve().parents[2] / "examples"
    table = Table(title="Available examples")
    table.add_column("Name")
    table.add_column("Path")
    for file in sorted(base.glob("*.yaml")):
        table.add_row(file.stem, str(file))
    console.print(table)


@examples_app.command("show")
def show_example(name: Annotated[str, typer.Argument(help="Example file stem")]) -> None:
    base = Path(__file__).resolve().parents[2] / "examples"
    target = base / f"{name}.yaml"
    if not target.exists():
        raise typer.BadParameter(f"Example not found: {name}")
    content = yaml.safe_load(_load_example(target))
    console.print_json(data=content)


def main() -> None:
    app()


if __name__ == "__main__":
    main()




