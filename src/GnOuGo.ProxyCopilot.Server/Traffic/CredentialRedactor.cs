using System.Text;
using System.Text.RegularExpressions;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Traffic;

public sealed partial class CredentialRedactor
{
    private readonly string[] _configured;
    public CredentialRedactor(ProxyOptions options)
    {
        _configured = options.Providers.Values.SelectMany(p => new[] { p.Connection.ApiKey, p.Connection.ClientSecret, p.Connection.PrivateKeyPem })
            .Concat(options.Providers.Values.Any(p => p.Authentication == ProxyAuthentication.CopilotEnvironment)
                ? [Environment.GetEnvironmentVariable("GITHUB_TOKEN"), Environment.GetEnvironmentVariable("COPILOT_API_KEY")] : [])
            .Where(s => !string.IsNullOrEmpty(s)).Cast<string>().Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    public string Redact(string text, IReadOnlyList<string>? credentials = null)
    {
        foreach (var secret in _configured.Concat(credentials ?? []).Where(s => s.Length > 0))
        {
            text = Hide(text, secret);
            // JSON bodies may contain escaped credentials, including multiline PEM keys.
            var json = System.Text.Json.Nodes.JsonValue.Create(secret)!.ToJsonString(ProxyJsonContext.Default.Options);
            text = Hide(text, json[1..^1]);
        }
        text = SensitiveJsonField().Replace(text, "$1[REDACTED]\"");
        text = Authorization().Replace(text, "$1[REDACTED]");
        return PrivateKey().Replace(text, "[REDACTED PRIVATE KEY]");
    }

    private static string Hide(string text, string secret)
    {
        text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        // Do not expose a credential's prefix when a capture ends mid-token.
        var maximum = Math.Min(secret.Length - 1, text.Length);
        for (var length = maximum; length > 0; length--)
            if (text.AsSpan(text.Length - length).SequenceEqual(secret.AsSpan(0, length)))
                return text[..^length] + "[REDACTED]";
        return text;
    }

    [GeneratedRegex("(\"(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|private[_-]?key(?:[_-]?pem)?|authorization)\"\\s*:\\s*\")(?:[^\"\\\\]|\\\\.)*(?:\"|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonField();
    [GeneratedRegex("((?:Bearer|Basic)\\s+)[A-Za-z0-9+/=_.-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Authorization();
    [GeneratedRegex("-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----[\\s\\S]*?(?:-----END (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();
}
