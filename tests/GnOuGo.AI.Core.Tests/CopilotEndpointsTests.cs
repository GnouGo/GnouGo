using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotEndpoints"/> URL builders.
/// </summary>
public class CopilotEndpointsTests
{
    [Fact]
    public void ChatCompletions_DefaultBase()
    {
        var url = CopilotEndpoints.ChatCompletions();
        Assert.Equal("https://models.github.ai/inference/chat/completions", url);
    }

    [Fact]
    public void ChatCompletions_CustomBase()
    {
        var url = CopilotEndpoints.ChatCompletions("https://my-proxy.example.com/v1");
        Assert.Equal("https://my-proxy.example.com/v1/chat/completions", url);
    }

    [Fact]
    public void ChatCompletions_AlreadyComplete()
    {
        var url = CopilotEndpoints.ChatCompletions("https://models.github.ai/inference/chat/completions");
        Assert.Equal("https://models.github.ai/inference/chat/completions", url);
    }

    [Fact]
    public void ChatCompletions_TrailingSlashHandled()
    {
        var url = CopilotEndpoints.ChatCompletions("https://models.github.ai/inference/");
        Assert.Equal("https://models.github.ai/inference/chat/completions", url);
    }
}

