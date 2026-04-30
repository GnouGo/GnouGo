import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, LLMToolCall, McpCallResult, McpGetPromptResult, McpPromptMessage, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def compile_main(yaml_text):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


async def run_main(yaml_text, mcp_factory, llm):
    engine = WorkflowEngine()
    engine.mcp_client_factory = mcp_factory
    engine.llm_client = llm
    return await engine.execute_async(compile_main(yaml_text), {})


class CaptureLlm:
    def __init__(self, *responses):
        self.requests = []
        self._responses = list(responses)

    async def call_async(self, request):
        self.requests.append(request)
        if self._responses:
            response = self._responses.pop(0)
            return response() if callable(response) else response
        return LLMResponse()


class Session:
    server_name = "github"

    def __init__(self):
        self.tool_calls = []
        self.prompt_calls = []
        self.list_tools_calls = 0
        self.list_prompts_calls = 0
        self.prompt_list_error = None

    async def list_tools_async(self):
        self.list_tools_calls += 1
        return [McpToolInfo(name="repo_stats", description="Return repository stars", input_schema={"type": "object"})]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        self.list_prompts_calls += 1
        if self.prompt_list_error:
            raise self.prompt_list_error
        return []

    async def call_tool_async(self, tool_name, arguments):
        self.tool_calls.append((tool_name, arguments))
        return McpCallResult(is_error=False, content={"stars": 42, "args": arguments})

    async def get_prompt_async(self, prompt_name, arguments):
        self.prompt_calls.append((prompt_name, arguments))
        return McpGetPromptResult(
            description="Summarize document",
            messages=[McpPromptMessage(role="user", content="Summary prompt resolved")],
        )


class Factory:
    def __init__(self, session):
        self.session = session
        self.server_metadata = []

    async def get_client_async(self, server_name):
        self.session.server_name = server_name
        return self.session


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_uses_provided_tools_and_calls_selected_tool():
    session = Session()
    llm = CaptureLlm(
        LLMResponse(
            text="I will call repo_stats.",
            tool_calls=[LLMToolCall(id="call_1", name="repo_stats", arguments={"owner": "me"})],
        )
    )

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: github
                  model: gpt-4o-mini
                  temperature: 0.2
                  prompt: Choose and call the best GitHub tool
                  tools:
                    - name: repo_stats
                      description: Return repository stars
                      input_schema:
                        type: object
                        properties:
                          owner: { type: string }
        """,
        Factory(session),
        llm,
    )

    assert result.success is True
    assert llm.requests[0].model == "gpt-4o-mini"
    assert llm.requests[0].temperature == 0.2
    assert llm.requests[0].tools[0].name == "repo_stats"
    assert session.tool_calls == [("repo_stats", {"owner": "me"})]
    out = result.step_results[0].output
    assert out["selection_mode"] == "llm"
    assert out["results"][0]["method"] == "repo_stats"


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_can_use_mcp_list_outputs_directly():
    session = Session()
    llm = CaptureLlm(LLMResponse(tool_calls=[LLMToolCall(name="repo_stats", arguments={"owner": "me"})]))

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: discover
                type: mcp.list
                input:
                  servers: [github]
                  include: [tools]
              - id: call
                type: mcp.call
                input:
                  server: github
                  model: gpt-4o-mini
                  prompt: Choose and call the best GitHub tool
                  tools: "${data.steps.discover.tools}"
        """,
        Factory(session),
        llm,
    )

    assert result.success is True
    assert session.list_tools_calls == 1
    assert session.tool_calls[0][0] == "repo_stats"


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_can_select_prompt_and_build_argument_schema():
    session = Session()
    llm = CaptureLlm(
        LLMResponse(tool_calls=[LLMToolCall(name="summarize_document", arguments={"text": "Hello"})])
    )

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: docs
                  kind: prompt
                  model: gpt-4o-mini
                  prompt: Choose and call the best document prompt
                  prompts:
                    - name: summarize_document
                      description: Summarize a document
                      arguments:
                        - name: text
                          description: Text to summarize
                          required: true
        """,
        Factory(session),
        llm,
    )

    assert result.success is True
    assert session.prompt_calls == [("summarize_document", {"text": "Hello"})]
    schema = llm.requests[0].tools[0].input_schema
    assert schema["properties"]["text"]["description"] == "Text to summarize"
    assert schema["required"] == ["text"]
    assert result.step_results[0].output["results"][0]["kind"] == "prompt"


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_prompt_fallback_unsupported_prompts_list_returns_no_caps():
    session = Session()
    session.prompt_list_error = RuntimeError("Method 'prompts/list' is not available.")
    llm = CaptureLlm()

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: docs
                  kind: prompt
                  model: gpt-4o-mini
                  prompt: Choose and call the best document prompt
        """,
        Factory(session),
        llm,
    )

    assert result.success is True
    assert llm.requests == []
    out = result.step_results[0].output
    assert out["status"] == "ok"
    assert out["selection_mode"] == "llm"
    assert out["tool_calls"] == []
    assert out["results"] == []


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_prompt_fallback_real_prompts_list_error_fails():
    session = Session()
    session.prompt_list_error = RuntimeError("connection failed")
    llm = CaptureLlm()

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: docs
                  kind: prompt
                  model: gpt-4o-mini
                  prompt: Choose and call the best document prompt
        """,
        Factory(session),
        llm,
    )

    assert result.success is False
    assert result.error.code == "MCP_PROMPT_ERROR"
    assert "connection failed" in result.error.message
    assert llm.requests == []


@pytest.mark.asyncio
async def test_mcp_call_llm_assisted_structured_output_runs_final_formatting_pass():
    session = Session()
    llm = CaptureLlm(
        LLMResponse(
            text="I will inspect rendered HTML.",
            tool_calls=[LLMToolCall(name="repo_stats", arguments={"owner": "me"})],
            usage={"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15},
        ),
        LLMResponse(text='{"stars":42}', json_payload={"stars": 42}, usage={"total_tokens": 28}),
    )

    result = await run_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: scrape
                type: mcp.call
                input:
                  server: github
                  model: gpt-4o-mini
                  prompt: Return repository stats as strict JSON.
                  tools:
                    - name: repo_stats
                      description: Return repository stars
                      input_schema: { type: object }
                  structured_output:
                    schema_inline:
                      type: object
                      properties:
                        stars: { type: number }
                      required: [stars]
                    strict: true
        """,
        Factory(session),
        llm,
    )

    assert result.success is True
    assert len(llm.requests) == 2
    assert llm.requests[0].tools is not None
    assert llm.requests[1].tools is None
    assert llm.requests[1].structured_output_schema["properties"]["stars"]["type"] == "number"
    assert "Do not invent facts or links" in llm.requests[1].prompt
    assert result.step_results[0].output["json"] == {"stars": 42}


