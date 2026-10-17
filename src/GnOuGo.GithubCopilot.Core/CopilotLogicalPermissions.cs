using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Refusals survive internal continuation; positive SDK-session grants do not.</summary>
public sealed class CopilotLogicalPermissions(Func<string, CancellationToken, Task> persistDenial)
{
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal async Task<bool> IsDeniedAsync(PermissionRequest request)
    {
        await _gate.WaitAsync();
        try { return _denied.Contains(Key(request)); }
        finally { _gate.Release(); }
    }
    internal async Task DenyAsync(PermissionRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var key = Key(request);
            if (!_denied.Add(key)) return;
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await persistDenial(key, flush.Token);
        }
        finally { _gate.Release(); }
    }
    private static string Key(PermissionRequest request)
    {
        JsonArray resource = request switch
        {
            PermissionRequestRead r => new(r.Kind, r.Path),
            PermissionRequestWrite r => new(r.Kind, r.FileName),
            PermissionRequestShell r => new(r.Kind, r.FullCommandText),
            PermissionRequestMcp r => new(r.Kind, r.ServerName, r.ToolName),
            PermissionRequestUrl r => new(r.Kind, r.Url),
            // A refused custom tool remains refused within the logical operation even if its arguments change.
            PermissionRequestCustomTool r => new(r.Kind, r.ToolName),
            _ => new(request.Kind)
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource.ToJsonString())));
    }
}
