using Microsoft.Extensions.Caching.Memory;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Centralized cache helper for MCP server capability listings (tools, resources, prompts).
/// Uses <see cref="IMemoryCache"/> with sliding expiration.
/// All methods are null-safe: when cache is null, they behave as a no-op / cache-miss.
/// </summary>
public static class McpCacheHelper
{
    public static readonly TimeSpan DefaultSlidingExpiration = TimeSpan.FromMinutes(5);

    // ── Keys ──

    public static string ToolsKey(string serverName) => $"gnougo-flow:mcp:{serverName}:tools";
    public static string ResourcesKey(string serverName) => $"gnougo-flow:mcp:{serverName}:resources";
    public static string PromptsKey(string serverName) => $"gnougo-flow:mcp:{serverName}:prompts";

    // ── Get (nullable) ──

    public static IReadOnlyList<McpToolInfo>? GetCachedTools(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(ToolsKey(serverName), out IReadOnlyList<McpToolInfo>? v) ? v : null;

    public static IReadOnlyList<McpResourceInfo>? GetCachedResources(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(ResourcesKey(serverName), out IReadOnlyList<McpResourceInfo>? v) ? v : null;

    public static IReadOnlyList<McpPromptInfo>? GetCachedPrompts(IMemoryCache? cache, string serverName)
        => cache != null && cache.TryGetValue(PromptsKey(serverName), out IReadOnlyList<McpPromptInfo>? v) ? v : null;

    // ── Set ──

    public static void CacheTools(IMemoryCache? cache, string serverName, IReadOnlyList<McpToolInfo> tools)
        => CacheTools(cache, serverName, tools, DefaultSlidingExpiration);

    public static void CacheTools(
        IMemoryCache? cache,
        string serverName,
        IReadOnlyList<McpToolInfo> tools,
        TimeSpan slidingExpiration)
        => CacheValue(cache, ToolsKey(serverName), tools, slidingExpiration);

    public static void CacheResources(IMemoryCache? cache, string serverName, IReadOnlyList<McpResourceInfo> resources)
        => CacheResources(cache, serverName, resources, DefaultSlidingExpiration);

    public static void CacheResources(
        IMemoryCache? cache,
        string serverName,
        IReadOnlyList<McpResourceInfo> resources,
        TimeSpan slidingExpiration)
        => CacheValue(cache, ResourcesKey(serverName), resources, slidingExpiration);

    public static void CachePrompts(IMemoryCache? cache, string serverName, IReadOnlyList<McpPromptInfo> prompts)
        => CachePrompts(cache, serverName, prompts, DefaultSlidingExpiration);

    public static void CachePrompts(
        IMemoryCache? cache,
        string serverName,
        IReadOnlyList<McpPromptInfo> prompts,
        TimeSpan slidingExpiration)
        => CacheValue(cache, PromptsKey(serverName), prompts, slidingExpiration);

    private static void CacheValue<T>(IMemoryCache? cache, string key, T value, TimeSpan slidingExpiration)
    {
        if (cache is null)
            return;

        var effectiveExpiration = slidingExpiration > TimeSpan.Zero
            ? slidingExpiration
            : DefaultSlidingExpiration;

        cache.Set(key, value, new MemoryCacheEntryOptions
        {
            SlidingExpiration = effectiveExpiration
        });
    }
}
