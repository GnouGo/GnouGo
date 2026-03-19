namespace GnOuGo.AI.Core;

/// <summary>
/// Image utility helpers for AI vision APIs.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Detects the MIME type of an image from its magic bytes.
    /// Falls back to "image/png" if unrecognized.
    /// </summary>
    public static string DetectMimeType(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 4 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
            return "image/webp";

        if (bytes.Length >= 4 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";

        return "image/png";
    }

    /// <summary>
    /// Builds a data URL (data:{mime};base64,...) from image bytes.
    /// </summary>
    public static string ToDataUrl(byte[] imageBytes)
    {
        var mimeType = DetectMimeType(imageBytes);
        var base64 = Convert.ToBase64String(imageBytes);
        return $"data:{mimeType};base64,{base64}";
    }
}

