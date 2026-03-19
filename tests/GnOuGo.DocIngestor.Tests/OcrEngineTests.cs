using System.Net;
using System.Text;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using DocIngestor.Core.Ocr;
using Moq;
using SkiaSharp;
using Xunit;

namespace DocIngestor.Tests;

public sealed class OcrEngineTests
{
    private static byte[] CreateSmallPng()
    {
        using var bmp = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── FakeOcrEngine ────────────────────────────────────────────────

    [Fact]
    public async Task FakeOcrEngine_ReturnsEmpty()
    {
        var engine = new FakeOcrEngine();
        var result = await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions());
        Assert.Equal(string.Empty, result);
    }

    // ── OpenAiVisionOcrEngine ────────────────────────────────────────

    [Fact]
    public async Task OpenAiVision_ReturnsExtractedText()
    {
        // Arrange: mock HTTP response like OpenAI chat completions
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "Hello World from OCR" }
                }
            }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);
        var apiKeyProvider = new StaticApiKeyProvider("test-key");

        var engine = new OpenAiVisionOcrEngine(
            endpointUrl: "https://api.openai.com/v1",
            model: "gpt-4o-mini",
            apiKeyProvider: apiKeyProvider,
            http: http);

        // Act
        var result = await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions("eng", 300));

        // Assert
        Assert.Equal("Hello World from OCR", result);

        // Verify the request was sent to the correct endpoint
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task OpenAiVision_EmptyImage_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler);
        var apiKeyProvider = new StaticApiKeyProvider("test-key");

        var engine = new OpenAiVisionOcrEngine("https://api.openai.com/v1", "gpt-4o-mini", apiKeyProvider, http);
        var result = await engine.RecognizeAsync(Array.Empty<byte>(), new OcrOptions());

        Assert.Equal(string.Empty, result);
        Assert.Null(handler.LastRequest); // no HTTP call made
    }

    [Fact]
    public async Task OpenAiVision_HttpError_ThrowsInvalidOperation()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_api_key\"}");
        var http = new HttpClient(handler);
        var apiKeyProvider = new StaticApiKeyProvider("bad-key");

        var engine = new OpenAiVisionOcrEngine("https://api.openai.com/v1", "gpt-4o-mini", apiKeyProvider, http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RecognizeAsync(CreateSmallPng(), new OcrOptions()).AsTask());
    }

    [Fact]
    public async Task OpenAiVision_EndpointWithoutV1_BuildsCorrectUrl()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "text" } }
            }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);
        var apiKeyProvider = new StaticApiKeyProvider("key");

        var engine = new OpenAiVisionOcrEngine("https://custom.api.com", "gpt-4o", apiKeyProvider, http);
        await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions());

        Assert.Equal("https://custom.api.com/v1/chat/completions", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task OpenAiVision_RequestContainsBase64Image()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "ok" } }
            }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);
        var apiKeyProvider = new StaticApiKeyProvider("key");

        var engine = new OpenAiVisionOcrEngine("https://api.openai.com/v1", "gpt-4o-mini", apiKeyProvider, http);
        await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions());

        // Verify the request body contains base64 image data
        var body = handler.LastRequestBody;
        Assert.NotNull(body);
        Assert.Contains("data:image/png;base64,", body);
        Assert.Contains("image_url", body);
    }

    // ── OllamaVisionOcrEngine ────────────────────────────────────────

    [Fact]
    public async Task OllamaVision_ReturnsExtractedText()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content = "Bonjour le monde OCR" }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);

        var engine = new OllamaVisionOcrEngine(
            baseUrl: "http://localhost:11434",
            model: "llava",
            http: http);

        var result = await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions("fra", 300));

        Assert.Equal("Bonjour le monde OCR", result);
        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task OllamaVision_EmptyImage_ReturnsEmpty()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var http = new HttpClient(handler);

        var engine = new OllamaVisionOcrEngine("http://localhost:11434", "llava", http);
        var result = await engine.RecognizeAsync(Array.Empty<byte>(), new OcrOptions());

        Assert.Equal(string.Empty, result);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task OllamaVision_HttpError_ThrowsInvalidOperation()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError, "model not found");
        var http = new HttpClient(handler);

        var engine = new OllamaVisionOcrEngine("http://localhost:11434", "llava", http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RecognizeAsync(CreateSmallPng(), new OcrOptions()).AsTask());
    }

    [Fact]
    public async Task OllamaVision_RequestContainsBase64InImagesArray()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content = "ok" }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);

        var engine = new OllamaVisionOcrEngine("http://localhost:11434", "llava", http);
        await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions());

        var body = handler.LastRequestBody;
        Assert.NotNull(body);
        Assert.Contains("\"images\"", body);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("\"model\":\"llava\"", body);
    }

    [Fact]
    public async Task OllamaVision_TrailingSlash_IsNormalized()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content = "ok" }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var http = new HttpClient(handler);

        var engine = new OllamaVisionOcrEngine("http://localhost:11434/", "llava", http);
        await engine.RecognizeAsync(CreateSmallPng(), new OcrOptions());

        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequest?.RequestUri?.ToString());
    }

    // ── Shared fake HTTP handler ─────────────────────────────────────

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}

