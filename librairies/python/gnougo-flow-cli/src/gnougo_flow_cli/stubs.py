from __future__ import annotations

from typing import Any

from gnougo_flow_core.models import (
    LLMRequest,
    LLMResponse,
    LLMToolCall,
    McpCallResult,
    McpGetPromptResult,
    McpPromptInfo,
    McpPromptMessage,
    McpResourceInfo,
    McpServerMetadata,
    McpToolInfo,
)


class EchoLLMClient:
    async def call_async(self, request: LLMRequest) -> LLMResponse:
        if "Generate a valid GnOuGo.Flow YAML document" in request.prompt:
            return LLMResponse(
                text=(
                    "version: 1\n"
                    "workflows:\n"
                    "  generated:\n"
                    "    steps:\n"
                    "      - id: answer\n"
                    "        type: set\n"
                    "        input:\n"
                    "          text: \"Stub generated workflow executed\"\n"
                    "    outputs:\n"
                    "      answer: \"${data.steps.answer.text}\"\n"
                )
            )

        if request.tools:
            return LLMResponse(
                text="I will call the first MCP capability",
                tool_calls=[
                    LLMToolCall(
                        id="stub-tool-call-1",
                        name=request.tools[0].name,
                        arguments={},
                    )
                ],
            )

        if request.structured_output_schema is not None:
            return LLMResponse(text='{"answer":"ok"}', json_payload={"answer": "ok"})

        return LLMResponse(
            text=f"[echo:{request.model}] {request.prompt}",
            json_payload={"echo": request.prompt, "model": request.model},
            usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        )


class DemoMcpSession:
    server_name = "demo"

    async def list_tools_async(self) -> list[McpToolInfo]:
        return [McpToolInfo(name="search", description="Search a local demo index")]

    async def list_resources_async(self) -> list[McpResourceInfo]:

        return [
            McpResourceInfo(
                uri="demo://docs",
                name="docs",
                description="Demo docs",
                mime_type="text/plain",
            )
        ]

    async def list_prompts_async(self) -> list[McpPromptInfo]:
        return [McpPromptInfo(name="summarize", description="Summarize text")]

    async def call_tool_async(self, tool_name: str, arguments: Any) -> McpCallResult:
        return McpCallResult(is_error=False, content={"tool": tool_name, "arguments": arguments})

    async def get_prompt_async(self, prompt_name: str, arguments: Any):
        return McpGetPromptResult(
            description=f"Prompt {prompt_name}",
            messages=[
                McpPromptMessage(
                    role="assistant",
                    content=f"prompt:{prompt_name}:{arguments}",
                )
            ],
        )


class DemoMcpFactory:
    def __init__(self) -> None:
        self.server_metadata = [
            McpServerMetadata(
                name="demo",
                description=(
                    "Demo MCP server exposing search/summarize capabilities for local CLI runs."
                ),
            )
        ]

    async def get_client_async(self, server_name: str):
        return DemoMcpSession()


class AutoApproveHumanProvider:
    async def request_input_async(self, request):
        return {"response": "approve", "run_id": request.run_id, "step_id": request.step_id}



