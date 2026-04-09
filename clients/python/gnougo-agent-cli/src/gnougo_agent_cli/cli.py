from __future__ import annotations

import asyncio
import json
from dataclasses import asdict
from pathlib import Path
from typing import Annotated

import typer
import yaml
from rich.console import Console
from rich.table import Table

from gnougo_flow_core import WorkflowCompiler, WorkflowEngine, WorkflowParser

from .mcp_real import RealMcpFactory
from .openai_client import OpenAiLlmClient
from .settings import load_settings
from .stubs import AutoApproveHumanProvider, DemoMcpFactory, EchoLLMClient
from .telemetry import OTelWorkflowTelemetry, TelemetryConfig, setup_tracing

app = typer.Typer(help="GnOuGo Agent CLI")
examples_app = typer.Typer(help="Examples management")
app.add_typer(examples_app, name="examples")
console = Console()


def _load_example(path: Path) -> str:
    return path.read_text(encoding="utf-8")


async def _run_async(
    workflow_file: Path,
    workflow_name: str | None,
    inputs: dict,
    otlp_endpoint: str | None,
    settings_file: Path | None,
    llm_mode: str,
    mcp_mode: str,
) -> int:
    setup_tracing(TelemetryConfig(otlp_endpoint=otlp_endpoint))
    settings = load_settings(settings_file)
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
                "OpenAI mode requires an API key. Set GNOUGO__OPENAI__API_KEY or settings.openai.api_key."
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
            raise typer.BadParameter("--mcp real requires configured stdio MCP servers in settings/appsettings")
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

    try:
        result = await engine.execute_async(compiled.workflows[target], inputs)
    finally:
        if used_real_factory:
            await real_factory.aclose()

    payload = {
        "success": result.success,
        "outputs": result.outputs,
        "error": asdict(result.error) if result.error else None,
        "steps": [step.model_dump() for step in result.step_results],
    }

    console.print_json(data=payload)
    return 0 if result.success else 1


@app.command("run")
def run_workflow(
    workflow_file: Annotated[Path, typer.Argument(help="Path to YAML workflow")],
    workflow: Annotated[str | None, typer.Option(help="Workflow name")] = None,
    inputs: Annotated[str, typer.Option(help="JSON object for inputs")] = "{}",
    otlp_endpoint: Annotated[str | None, typer.Option(help="OTLP HTTP endpoint")] = None,
    settings_file: Annotated[Path | None, typer.Option("--settings", help="Path to settings.json|yaml")]=None,
    llm: Annotated[str, typer.Option("--llm", help="LLM backend: auto | openai | stub")] = "auto",
    mcp: Annotated[str, typer.Option("--mcp", help="MCP backend: auto | real | stub")] = "auto",
) -> None:
    llm_mode = llm.lower().strip()
    if llm_mode not in {"auto", "openai", "stub"}:
        raise typer.BadParameter("--llm must be one of: auto, openai, stub")
    mcp_mode = mcp.lower().strip()
    if mcp_mode not in {"auto", "real", "stub"}:
        raise typer.BadParameter("--mcp must be one of: auto, real, stub")
    parsed_inputs = json.loads(inputs)
    code = asyncio.run(_run_async(workflow_file, workflow, parsed_inputs, otlp_endpoint, settings_file, llm_mode, mcp_mode))
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




