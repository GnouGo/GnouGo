namespace GnOuGo.Agent.Server.Components.Pages;

internal static class ChatComposerText
{
    public static string PreserveForSubmission(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
