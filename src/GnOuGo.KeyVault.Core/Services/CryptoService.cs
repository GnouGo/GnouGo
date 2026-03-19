using System.Security.Cryptography;
using System.Text;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Hybrid RSA + AES encryption service.
/// RSA encrypts a random AES-256 key; AES-GCM encrypts the actual payload.
/// This removes the RSA size limit (~245 bytes for RSA-2048 OAEP-SHA256).
/// 
/// Wire format (base64 of): [2 bytes RSA-encrypted-key-length][RSA-encrypted AES key][12 bytes nonce][16 bytes tag][ciphertext]
/// </summary>
public static class CryptoService
{
    private const int AesKeySize = 256;
    private const int NonceSize = 12;  // AES-GCM standard
    private const int TagSize = 16;    // AES-GCM standard

    /// <summary>Generates a new RSA-2048 key pair and returns (publicPem, privatePem).</summary>
    public static (string PublicPem, string PrivatePem) GenerateKeyPair(int keySizeInBits = 2048)
    {
        using var rsa = RSA.Create(keySizeInBits);
        var pub = rsa.ExportRSAPublicKeyPem();
        var priv = rsa.ExportRSAPrivateKeyPem();
        return (pub, priv);
    }

    /// <summary>Encrypts a UTF-8 string value with hybrid RSA+AES-GCM. Returns base64.</summary>
    public static string Encrypt(string plainText, string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);

        // Generate random AES-256 key
        var aesKey = RandomNumberGenerator.GetBytes(AesKeySize / 8);

        // RSA-encrypt the AES key
        var encryptedAesKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

        // AES-GCM encrypt the plaintext
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(aesKey, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        }

        // Wire format: [2B encKeyLen][encKey][12B nonce][16B tag][ciphertext]
        var encKeyLen = (ushort)encryptedAesKey.Length;
        var result = new byte[2 + encryptedAesKey.Length + NonceSize + TagSize + ciphertext.Length];
        var offset = 0;

        BitConverter.TryWriteBytes(result.AsSpan(offset, 2), encKeyLen);
        offset += 2;

        encryptedAesKey.CopyTo(result, offset);
        offset += encryptedAesKey.Length;

        nonce.CopyTo(result, offset);
        offset += NonceSize;

        tag.CopyTo(result, offset);
        offset += TagSize;

        ciphertext.CopyTo(result, offset);

        return Convert.ToBase64String(result);
    }

    /// <summary>Decrypts a base64 hybrid RSA+AES-GCM encrypted value.</summary>
    public static string Decrypt(string base64Encrypted, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var data = Convert.FromBase64String(base64Encrypted);
        var offset = 0;

        // Read RSA-encrypted key length
        var encKeyLen = BitConverter.ToUInt16(data, offset);
        offset += 2;

        // Decrypt AES key
        var encryptedAesKey = data.AsSpan(offset, encKeyLen);
        offset += encKeyLen;
        var aesKey = rsa.Decrypt(encryptedAesKey.ToArray(), RSAEncryptionPadding.OaepSHA256);

        // Read nonce, tag, ciphertext
        var nonce = data.AsSpan(offset, NonceSize);
        offset += NonceSize;

        var tag = data.AsSpan(offset, TagSize);
        offset += TagSize;

        var ciphertext = data.AsSpan(offset);
        var plainBytes = new byte[ciphertext.Length];

        using (var aes = new AesGcm(aesKey, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }
}

