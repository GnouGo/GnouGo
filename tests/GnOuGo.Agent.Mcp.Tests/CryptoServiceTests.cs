using GnOuGo.KeyVault.Core.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public sealed class CryptoServiceTests
{
    [Fact]
    public void GenerateKeyPair_ReturnsValidPemStrings()
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();

        Assert.StartsWith("-----BEGIN RSA PUBLIC KEY-----", pub);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", priv);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ShortText()
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();
        const string original = "hello-secret-123";

        var encrypted = CryptoService.Encrypt(original, pub);
        var decrypted = CryptoService.Decrypt(encrypted, priv);

        Assert.Equal(original, decrypted);
        Assert.NotEqual(original, encrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_LongText()
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();
        var original = new string('A', 5000) + "🔐" + new string('Z', 5000);

        var encrypted = CryptoService.Encrypt(original, pub);
        var decrypted = CryptoService.Decrypt(encrypted, priv);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_EmptyString()
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();

        var encrypted = CryptoService.Encrypt("", pub);
        var decrypted = CryptoService.Decrypt(encrypted, priv);

        Assert.Equal("", decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_Unicode()
    {
        var (pub, priv) = CryptoService.GenerateKeyPair();
        const string original = "Clé secrète: été → hiver 🌍 日本語 中文";

        var encrypted = CryptoService.Encrypt(original, pub);
        var decrypted = CryptoService.Decrypt(encrypted, priv);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Encrypt_DifferentCiphertextEachTime()
    {
        var (pub, _) = CryptoService.GenerateKeyPair();
        const string original = "same-value";

        var enc1 = CryptoService.Encrypt(original, pub);
        var enc2 = CryptoService.Encrypt(original, pub);

        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var (pub1, _) = CryptoService.GenerateKeyPair();
        var (_, priv2) = CryptoService.GenerateKeyPair();

        var encrypted = CryptoService.Encrypt("secret", pub1);

        Assert.ThrowsAny<Exception>(() => CryptoService.Decrypt(encrypted, priv2));
    }

    [Fact]
    public void GenerateKeyPair_DifferentEachTime()
    {
        var (pub1, _) = CryptoService.GenerateKeyPair();
        var (pub2, _) = CryptoService.GenerateKeyPair();

        Assert.NotEqual(pub1, pub2);
    }
}


