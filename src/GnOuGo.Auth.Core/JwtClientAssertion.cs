using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GnOuGo.Auth.Core;

internal static class JwtClientAssertion
{
    public static string CreateRs256(
        string clientId,
        string tokenEndpoint,
        string privateKeyPem,
        TimeSpan? lifetime = null)
    {
        lifetime ??= TimeSpan.FromMinutes(5);

        var now = DateTimeOffset.UtcNow;
        var iat = now.ToUnixTimeSeconds();
        var exp = now.Add(lifetime.Value).ToUnixTimeSeconds();
        var jti = Guid.NewGuid().ToString("N");

        var headerBytes = JsonUtf8(writer =>
        {
            writer.WriteString("alg", "RS256");
            writer.WriteString("typ", "JWT");
        });

        var payloadBytes = JsonUtf8(writer =>
        {
            writer.WriteString("iss", clientId);
            writer.WriteString("sub", clientId);
            writer.WriteString("aud", tokenEndpoint);
            writer.WriteNumber("iat", iat);
            writer.WriteNumber("exp", exp);
            writer.WriteString("jti", jti);
        });

        var signingInput = $"{B64Url(headerBytes)}.{B64Url(payloadBytes)}";
        var signingBytes = Encoding.ASCII.GetBytes(signingInput);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.AsSpan());

        var sig = rsa.SignData(signingBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(sig)}";
    }

    private static byte[] JsonUtf8(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

