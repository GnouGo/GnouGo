using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Ocr;
using DocIngestor.Core.Reranking;
using GnOuGo.AI.Core;
using GnOuGo.Auth.Core;
using SkiaSharp;
using Xunit;

namespace DocIngestor.Tests;

public sealed class NativeGenerationTransportTests
{
    [Theory]
    [InlineData("openai", false, false)] [InlineData("ollama", false, false)]
    [InlineData("openai", true, false)] [InlineData("ollama", true, false)]
    [InlineData("openai", false, true)] [InlineData("ollama", false, true)]
    [InlineData("openai", true, true)] [InlineData("ollama", true, true)]
    public async Task AuxiliaryChatAndVisionUseNativeStreamingAndRejectTruncatedOutput(string type, bool vision, bool truncated)
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new Handler(type, vision, truncated);
        using var http = new HttpClient(handler);
        var provider = new ModelProviderOptions { RequestPolicy = new() { StreamingMode = LLMStreamingMode.Streaming,
            StreamingSupport = new() { [type == "openai" ? LLMTransportResolver.ChatCompletions : LLMTransportResolver.OllamaChat] = true } } };
        async Task<string> Run()
        {
            if (!vision)
            {
                IChatScorer scorer = type == "openai"
                    ? new OpenAiChatScorer("https://gateway.example/v1", "deployment", new StaticApiKeyProvider("test"), http, providerOptions: provider)
                    : new OllamaChatScorer("https://gateway.example", "deployment", http, providerOptions: provider);
                return (await scorer.ScoreAsync("query", "passage", ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            IOcrEngine engine = type == "openai"
                ? new OpenAiVisionOcrEngine("https://gateway.example/v1", "deployment", new StaticApiKeyProvider("test"), http, providerOptions: provider)
                : new OllamaVisionOcrEngine("https://gateway.example", "deployment", http, providerOptions: provider);
            using var bitmap = new SKBitmap(2, 2);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return await engine.RecognizeAsync(encoded.ToArray(), new OcrOptions(), ct);
        }
        if (truncated) await Assert.ThrowsAsync<HttpRequestException>(() => Run());
        else Assert.Equal("8", await Run());
        Assert.Equal(1, handler.Calls);
    }
    private sealed class Handler(string type, bool vision, bool truncated) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("gateway.example", request.RequestUri!.Host);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.True(body["stream"]!.GetValue<bool>());
            if (vision) Assert.Contains(type == "openai" ? "image_url" : "images", body["messages"]!.ToJsonString());
            var text = type == "openai"
                ? "data: {\"id\":\"r\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"8\"}}]}\n\n" + (truncated ? "" : "data: {\"id\":\"r\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")
                : "{\"message\":{\"content\":\"8\"},\"done\":false}\n" + (truncated ? "" : "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n");
            return new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type == "openai" ? "text/event-stream" : "application/x-ndjson") };
        }
    }
}
