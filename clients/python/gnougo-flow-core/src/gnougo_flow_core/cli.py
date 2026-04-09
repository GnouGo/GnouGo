from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path

from .compilation import WorkflowCompiler
from .parsing import WorkflowParser
from .runtime import WorkflowEngine


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="gnougo-flow", description="Run GnOuGo Flow workflows")
    parser.add_argument("yaml_path", type=Path, help="Path to workflow YAML file")
    parser.add_argument("--workflow", default=None, help="Workflow name (default: entrypoint/main)")
    parser.add_argument("--inputs", default="{}", help="JSON object for workflow inputs")
    return parser


async def _run(args: argparse.Namespace) -> int:
    yaml_text = args.yaml_path.read_text(encoding="utf-8")
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)
    target = args.workflow or compiled.entrypoint
    if not target or target not in compiled.workflows:
        raise ValueError(f"Workflow '{target}' not found")

    inputs = json.loads(args.inputs)
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows[target], inputs)
    payload = {
        "success": result.success,
        "outputs": result.outputs,
        "error": result.error.model_dump() if result.error else None,
        "steps": [step.model_dump() for step in result.step_results],
    }
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 0 if result.success else 1


def main() -> None:
    parser = _build_parser()
    args = parser.parse_args()
    raise SystemExit(asyncio.run(_run(args)))


if __name__ == "__main__":
    main()

