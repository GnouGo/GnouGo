using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Cli;

/// <summary>
/// Mock LLM client that returns deterministic responses for testing.
/// </summary>
internal sealed class MockLLMClient : ILLMClient
{
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var prompt = request.Prompt;
        var model = request.Model;

        var response = new LLMResponse
        {
            Text = $"[Mock {model}] Response to: {Truncate(prompt, 100)}",
            Usage = new JsonObject
            {
                ["prompt_tokens"] = 10,
                ["completion_tokens"] = 20,
                ["total_tokens"] = 30
            }
        };

        return Task.FromResult(response);
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}

