using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GnOuGo.Files.Core;

internal static class EncryptedFileContent
{
    private const int BlockSize = 128 * 1024;
    private const int TagSize = 16;

    // Each object has a fresh key. Its authenticated manifest binds this key to the tenant,
    // object, exact length and digest. The nonce binds each frame to its position.
    public static async Task<(long Size, string Digest)> EncryptAsync(Stream input, Stream output,
        byte[] key, long limit, CancellationToken ct)
    {
        using var cipher = new AesGcm(key, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var plain = new byte[BlockSize];
        var frame = new byte[BlockSize + TagSize];
        var nonce = new byte[12];
        long size = 0;
        for (long index = 0; ; index++)
        {
            var length = await input.ReadAtLeastAsync(plain, BlockSize, false, ct);
            if (length == 0) break;
            size = checked(size + length);
            if (size > limit) throw new ArgumentException("The file exceeds the upload limit.");
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
            cipher.Encrypt(nonce, plain.AsSpan(0, length), frame.AsSpan(0, length), frame.AsSpan(length, TagSize));
            hash.AppendData(plain, 0, length);
            await output.WriteAsync(frame.AsMemory(0, length + TagSize), ct);
        }
        return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public static async Task DecryptAsync(Stream input, Stream output, FileManifest file, CancellationToken ct)
    {
        using var cipher = new AesGcm(file.Key, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var plain = new byte[BlockSize];
        var frame = new byte[BlockSize + TagSize];
        var nonce = new byte[12];
        if (input.Length != file.Size + (file.Size + BlockSize - 1) / BlockSize * TagSize)
            throw new InvalidDataException("Encrypted file length mismatch.");
        for (long offset = 0; offset < file.Size; offset += BlockSize)
        {
            var length = (int)Math.Min(BlockSize, file.Size - offset);
            await input.ReadExactlyAsync(frame.AsMemory(0, length + TagSize), ct);
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), offset / BlockSize);
            cipher.Decrypt(nonce, frame.AsSpan(0, length), frame.AsSpan(length, TagSize), plain.AsSpan(0, length));
            hash.AppendData(plain, 0, length);
            await output.WriteAsync(plain.AsMemory(0, length), ct);
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Digest)
            throw new InvalidDataException("Encrypted file digest mismatch.");
    }
}
