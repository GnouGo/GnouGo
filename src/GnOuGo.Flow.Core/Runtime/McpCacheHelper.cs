using Microsoft.Extensions.Caching.Memory;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Centralized cache helper for MCP server capability listings (tools, resources, prompts).
/// Uses <see cref="IMemoryCache"/> with sliding expiration.
/// All methods are null-safe: when cache is null, they behave as a no-op / cache-miss.
/// </summary>
internal static class McpCacheHelper
{
    internal static readonly MemoryCacheEntryOptions CacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(5)
    };

    // ── Keys ──

    internal static string ToolsKey(string serverName) => $"gnougo-flow:mcp:{serverName}:tools";
    internal static string ResourcesKey(string serverName) => $"gnougo-flow:mcp:{serverName}:resources";
    internal static string PromptsKey(string serverName) => $"gnougo-flow:mcp:{serverName}:prompts";

    // ── Get (nullable) ──

    internal static IReadOnlyList<McpToolInfo>? GetCachedTools(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(ToolsKey(serverName), out IReadOnlyList<McpToolInfo>? v) ? v : null;

    internal static IReadOnlyList<McpResourceInfo>? GetCachedResources(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(ResourcesKey(serverName), out IReadOnlyList<McpResourceInfo>? v) ? v : null;

    internal static IReadOnlyList<McpPromptInfo>? GetCachedPrompts(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(PromptsKey(serverName), out IReadOnlyList<McpPromptInfo>? v) ? v : null;

    // ── Set ──

    internal static void CacheTools(IMemoryCache? cache, string serverName, IReadOnlyList<McpToolInfo> tools)
        => cache?.Set(ToolsKey(serverName), tools, CacheOptions);

    internal static void CacheResources(IMemoryCache? cache, string serverName, IReadOnlyList<McpResourceInfo> resources)
        => cache?.Set(ResourcesKey(serverName), resources, CacheOptions);

    internal static void CachePrompts(IMemoryCache? cache, string serverName, IReadOnlyList<McpPromptInfo> prompts)
        => cache?.Set(PromptsKey(serverName), prompts, CacheOptions);
}

