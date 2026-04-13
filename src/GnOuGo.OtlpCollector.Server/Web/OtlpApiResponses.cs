using System.Text.Json.Serialization.Metadata;

namespace OtlpTenantCollector.Web;

public static class OtlpApiResponses
{
    public static Task ExecuteAsync(HttpContext httpContext, IResult result)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(result);
        return result.ExecuteAsync(httpContext);
    }

    public static IResult Json<TValue>(TValue value, JsonTypeInfo<TValue> typeInfo, int statusCode = StatusCodes.Status200OK)
        => Results.Json(value, typeInfo, statusCode: statusCode);

    public static IResult BadRequest(string error)
        => Results.Json(
            new ApiErrorResponse(error),
            OtlpApiJsonContext.Default.ApiErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string error)
        => Results.Json(
            new ApiErrorResponse(error),
            OtlpApiJsonContext.Default.ApiErrorResponse,
            statusCode: StatusCodes.Status404NotFound);

    public static IResult OkMessage(string message)
        => Results.Json(
            new ApiMessageResponse(message),
            OtlpApiJsonContext.Default.ApiMessageResponse,
            statusCode: StatusCodes.Status200OK);

    public static async Task WriteServerSentEventAsync<TValue>(
        HttpContext httpContext,
        string eventName,
        TValue payload,
        JsonTypeInfo<TValue> typeInfo,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, typeInfo);
        await httpContext.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    public static async Task WriteJsonResponseAsync<TValue>(
        HttpResponse response,
        TValue payload,
        JsonTypeInfo<TValue> typeInfo,
        int statusCode,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var json = System.Text.Json.JsonSerializer.Serialize(payload, typeInfo);
        await response.WriteAsync(json, cancellationToken);
    }
}



