namespace GnOuGo.ProxyCopilot.Server;

public sealed class ProxyException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
