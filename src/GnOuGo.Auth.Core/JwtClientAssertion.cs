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

        // header
        var headerBytes = JsonUtf8(new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        });

        // payload
        var payloadBytes = JsonUtf8(new Dictionary<string, object>
        {
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["aud"] = tokenEndpoint,
            ["iat"] = iat,
            ["exp"] = exp,
            ["jti"] = jti
        });

        var signingInput = $"{B64Url(headerBytes)}.{B64Url(payloadBytes)}";
        var signingBytes = Encoding.ASCII.GetBytes(signingInput);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.AsSpan());

        var sig = rsa.SignData(signingBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(sig)}";
    }

    private static byte[] JsonUtf8(Dictionary<string, object> obj)
        => JsonSerializer.SerializeToUtf8Bytes(obj);

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

