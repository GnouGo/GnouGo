using System;
using System.Collections.Concurrent;

namespace GnOuGo.Agent.Server.Hosting;

public static class DesktopWebViewTracker
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> PageLoadedTokens = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ClientReadyTokens = new(StringComparer.Ordinal);

    public static void MarkPageLoaded(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        PageLoadedTokens[token] = DateTimeOffset.UtcNow;
        CleanupExpired();
    }

    public static bool IsPageLoaded(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        CleanupExpired();
        return PageLoadedTokens.ContainsKey(token);
    }

    public static void MarkClientReady(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        ClientReadyTokens[token] = DateTimeOffset.UtcNow;
        CleanupExpired();
    }

    public static bool IsClientReady(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        CleanupExpired();
        return ClientReadyTokens.ContainsKey(token);
    }

    private static void CleanupExpired()
    {
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var pair in ClientReadyTokens)
        {
            if (pair.Value < threshold)
            {
                ClientReadyTokens.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in PageLoadedTokens)
        {
            if (pair.Value < threshold)
            {
                PageLoadedTokens.TryRemove(pair.Key, out _);
            }
        }
    }
}

