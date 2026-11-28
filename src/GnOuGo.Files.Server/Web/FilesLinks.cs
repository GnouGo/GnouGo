namespace GnOuGo.Files.Server.Web;

public static class FilesLinks
{
    private const string Route = "/api/files/";

    public static Uri? ParsePublicBase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Files:PublicBaseUrl requires an absolute HTTP(S) base URL.");
        return uri;
    }

    public static string DownloadUrl(Uri? publicBase, HttpRequest request, string id) =>
        (publicBase?.AbsoluteUri.TrimEnd('/') ?? $"{request.Scheme}://{request.Host}{request.PathBase}") + Route + id;

    public static string ResolveUploadId(string value, Uri? publicBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var id = value;
        if (value.StartsWith(Route, StringComparison.Ordinal)) id = value[Route.Length..];
        else if (publicBase is not null)
        {
            var prefix = publicBase.AbsoluteUri.TrimEnd('/') + Route;
            if (value.StartsWith(prefix, StringComparison.Ordinal)) id = value[prefix.Length..];
        }
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("Select an uploaded file or its configured Files download URL.");
        return id;
    }
}
