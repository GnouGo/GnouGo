namespace GnOuGo.ProxyCopilot.Server.Configuration;

internal static class UpstreamUrlTemplate
{
    private const string ModelName = "{model_name}";

    public static bool IsValid(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        var expanded = template.Replace(ModelName, "model", StringComparison.Ordinal);
        if (expanded.IndexOfAny(['{', '}']) >= 0
            || expanded.Contains("%7b", StringComparison.OrdinalIgnoreCase)
            || expanded.Contains("%7d", StringComparison.OrdinalIgnoreCase)
            || !ModelRegistry.IsEndpoint(expanded)) return false;

        var placeholder = template.IndexOf(ModelName, StringComparison.Ordinal);
        if (placeholder < 0) return true;
        // A model may select a path, never the destination host or credentials.
        var path = template.IndexOf('/', template.IndexOf("://", StringComparison.Ordinal) + 3);
        return path >= 0 && placeholder > path;
    }

    public static bool AcceptsModel(string template, string model)
        => !template.Contains(ModelName, StringComparison.Ordinal) || model is not ("." or "..");

    public static string Resolve(string template, string upstreamId)
        => template.Replace(ModelName, Uri.EscapeDataString(upstreamId), StringComparison.Ordinal);
}
