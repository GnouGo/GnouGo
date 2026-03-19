namespace DocIngestor.Core.Images;

public static class ImageHeaderParser
{
    public static (int? width, int? height) TryGetSize(string contentType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10) return (null, null);

        // PNG: width/height are 4 bytes each at IHDR chunk
        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) &&
            bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            // IHDR starts at byte 12, width at 16, height at 20
            int w = ReadBE32(bytes.Slice(16, 4));
            int h = ReadBE32(bytes.Slice(20, 4));
            return (w, h);
        }

        // JPEG: scan segments until SOF0/SOF2
        if ((contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
             contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)) &&
            bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                byte marker = bytes[i + 1];
                // SOF0/2
                if (marker is 0xC0 or 0xC2)
                {
                    int h = (bytes[i + 5] << 8) + bytes[i + 6];
                    int w = (bytes[i + 7] << 8) + bytes[i + 8];
                    return (w, h);
                }
                // segment length
                if (i + 3 >= bytes.Length) break;
                int len = (bytes[i + 2] << 8) + bytes[i + 3];
                if (len <= 0) break;
                i += 2 + len;
            }
        }

        return (null, null);
    }

    private static int ReadBE32(ReadOnlySpan<byte> b)
        => (b[0] << 24) + (b[1] << 16) + (b[2] << 8) + b[3];
}
