import asyncio
import sys
import textwrap

from gnougo_flow_cli.mcp_real import RealMcpSession
from gnougo_flow_cli.settings import McpServerSettings


def test_sdk_stdio_discovery_call_prompt_and_reconnect() -> None:
    """Exercise the real upgraded SDK on both sides of the stdio boundary."""
    server = textwrap.dedent('''
        import asyncio
        from mcp import types
        from mcp.server import Server
        from mcp.server.stdio import stdio_server

        async def list_tools(context, params):
            return types.ListToolsResult(tools=[types.Tool(
                name="echo", description="Echo text",
                inputSchema={"type": "object", "properties": {"text": {"type": "string"}}}
            )])

        async def call_tool(context, params):
            return types.CallToolResult(is_error=params.arguments["text"] == "fail", content=[
                types.TextContent(type="text", text=params.arguments["text"])
            ])

        async def list_resources(context, params):
            return types.ListResourcesResult(resources=[types.Resource(
                uri="test://readme", name="readme", mimeType="text/plain"
            )])

        async def list_prompts(context, params):
            return types.ListPromptsResult(prompts=[types.Prompt(
                name="greet", arguments=[types.PromptArgument(name="name", required=True)]
            )])

        async def get_prompt(context, params):
            return types.GetPromptResult(messages=[types.PromptMessage(
                role="user", content=types.TextContent(type="text", text=params.arguments["name"])
            )])

        async def main():
            server = Server("sdk-smoke", on_list_tools=list_tools, on_call_tool=call_tool,
                            on_list_resources=list_resources, on_list_prompts=list_prompts,
                            on_get_prompt=get_prompt)
            async with stdio_server() as (reader, writer):
                await server.run(reader, writer, server.create_initialization_options())

        asyncio.run(main())
    ''')

    async def exercise() -> None:
        session = RealMcpSession("smoke", McpServerSettings(command=sys.executable,
                                                         args=["-c", server]))
        try:
            tools = await session.list_tools_async()
            assert [tool.name for tool in tools] == ["echo"]
            assert tools[0].input_schema["properties"]["text"]["type"] == "string"
            result = await session.call_tool_async("echo", {"text": "sdk round trip"})
            assert not result.is_error
            assert result.content[0]["text"] == "sdk round trip"
            failed = await session.call_tool_async("echo", {"text": "fail"})
            assert failed.is_error
            resources = await session.list_resources_async()
            assert resources[0].uri == "test://readme"
            assert resources[0].mime_type == "text/plain"
            prompts = await session.list_prompts_async()
            assert prompts[0].arguments[0].required
            prompt = await session.get_prompt_async("greet", {"name": "test user"})
            assert prompt.messages[0].content == "test user"
            await session.aclose()
            assert (await session.list_tools_async())[0].name == "echo"
        finally:
            await session.aclose()

    asyncio.run(asyncio.wait_for(exercise(), timeout=20))
