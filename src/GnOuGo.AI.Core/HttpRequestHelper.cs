using System.Net.Http.Headers;
using System.Text;

namespace GnOuGo.AI.Core;

/// <summary>
/// Reusable helpers for building and sending HTTP requests to AI APIs.
/// </summary>
public static class HttpRequestHelper
{
    /// <summary>Creates a GET request.</summary>
    public static HttpRequestMessage CreateGet(string url)
        => new(HttpMethod.Get, url);

    /// <summary>Creates a POST request with JSON payload.</summary>
    public static HttpRequestMessage CreateJsonPost(string url, byte[] jsonPayload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(jsonPayload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return req;
    }

    /// <summary>Sets the Bearer authorization header on the request.</summary>
    public static void SetBearerAuth(HttpRequestMessage req, string apiKey)
        => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    /// <summary>Reads the response body as a string (for error reporting).</summary>
    public static async Task<string> ReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct = default)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}

