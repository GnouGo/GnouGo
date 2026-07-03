from __future__ import annotations

# Keep this list in sync with registered executors and parser/compiler validation.
STEP_TYPES = frozenset(
    {
        "sequence",
        "parallel",
        "loop.sequential",
        "loop.parallel",
        "switch",
        "set",
        "assert.non_null",
        "template.render",
        "llm.call",
        "workflow.call",
        "workflow.plan",
        "workflow.route",
        "workflow.execute",
        "mcp.call",
        "mcp.list",
        "emit",
        "human.input",
    }
)
