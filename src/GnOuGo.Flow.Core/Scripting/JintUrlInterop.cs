using System.Globalization;
using System.Text.Json.Nodes;
using Jint;

namespace GnOuGo.Flow.Core.Scripting;

internal static class JintUrlInterop
{
    private const string ParseUrlFunctionName = "__gnougo_parse_url";

    private const string UrlPolyfillScript = """
        (function (global) {
          function URL(input, base) {
            if (!(this instanceof URL)) {
              return new URL(input, base);
            }

            var parsed = JSON.parse(__gnougo_parse_url(String(input), base == null ? "" : String(base)));
            if (!parsed.ok) {
              throw new TypeError("Invalid URL");
            }

            Object.defineProperties(this, {
              href: { value: parsed.href, enumerable: true },
              protocol: { value: parsed.protocol, enumerable: true },
              username: { value: parsed.username, enumerable: true },
              password: { value: parsed.password, enumerable: true },
              host: { value: parsed.host, enumerable: true },
              hostname: { value: parsed.hostname, enumerable: true },
              port: { value: parsed.port, enumerable: true },
              pathname: { value: parsed.pathname, enumerable: true },
              search: { value: parsed.search, enumerable: true },
              hash: { value: parsed.hash, enumerable: true },
              origin: { value: parsed.origin, enumerable: true }
            });
          }

          URL.prototype.toString = function () { return this.href; };
          URL.prototype.toJSON = function () { return this.href; };
          global.URL = URL;
        })(globalThis);
        """;

    public static void Install(Engine engine)
    {
#pragma warning disable IL2026, IL2111 // Jint delegate interop
        engine.SetValue(ParseUrlFunctionName, new Func<string, string, string>(ParseUrl));
#pragma warning restore IL2026, IL2111
        engine.Execute(UrlPolyfillScript);
    }

    private static string ParseUrl(string input, string baseUrl)
    {
        try
        {
            if (!TryCreateUri(input, baseUrl, out var uri))
                return Failure();

            var userInfo = uri.UserInfo;
            var username = "";
            var password = "";
            if (!string.IsNullOrEmpty(userInfo))
            {
                var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
                if (separator >= 0)
                {
                    username = Uri.UnescapeDataString(userInfo[..separator]);
                    password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
                }
                else
                {
                    username = Uri.UnescapeDataString(userInfo);
                }
            }

            var host = uri.IsDefaultPort
                ? uri.Host
                : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";

            return new JsonObject
            {
                ["ok"] = true,
                ["href"] = uri.AbsoluteUri,
                ["protocol"] = uri.Scheme + ":",
                ["username"] = username,
                ["password"] = password,
                ["host"] = host,
                ["hostname"] = uri.Host,
                ["port"] = uri.IsDefaultPort ? "" : uri.Port.ToString(CultureInfo.InvariantCulture),
                ["pathname"] = uri.AbsolutePath,
                ["search"] = uri.Query,
                ["hash"] = uri.Fragment,
                ["origin"] = string.IsNullOrEmpty(uri.Host)
                    ? "null"
                    : $"{uri.Scheme}://{host}"
            }.ToJsonString();
        }
        catch (UriFormatException)
        {
            return Failure();
        }
    }

    private static bool TryCreateUri(string input, string baseUrl, out Uri uri)
    {
        uri = null!;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                return false;
            if (!Uri.TryCreate(baseUri, input, out var relativeUri) || !relativeUri.IsAbsoluteUri)
                return false;

            uri = relativeUri;
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var absoluteUri) || !absoluteUri.IsAbsoluteUri)
            return false;

        uri = absoluteUri;
        return true;
    }

    private static string Failure() =>
        new JsonObject { ["ok"] = false }.ToJsonString();
}
