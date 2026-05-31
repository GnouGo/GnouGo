
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Auth.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for Anthropic models through the Anthropic Messages API.
/// </summary>
public sealed class AnthropicLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
	public const string DefaultEndpoint = "https://api.anthropic.com/v1";
	private const string AnthropicVersion = "2023-06-01";

	private static readonly TimeSpan BackgroundInitialPollDelay = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan BackgroundMaxPollDelay = TimeSpan.FromSeconds(15);

	private readonly HttpClient _http;
	private readonly ILogger<AnthropicLLMProvider> _logger;

	public AnthropicLLMProvider(HttpClient http, ILogger<AnthropicLLMProvider>? logger = null)
	{
		_http = http;
		_logger = logger ?? NullLogger<AnthropicLLMProvider>.Instance;
		LLMHttpClientDefaults.EnsureMinimumTimeout(_http);
	}

	/// <summary>
	/// Canonical provider type handled by this provider implementation.
	/// </summary>
	public string ProviderType => "anthropic";

	/// <summary>
	/// Sends a chat request to the Anthropic Messages API.
	/// When <see cref="LLMClientRequest.UseBackgroundMode"/> is set, uses the Message Batches API
	/// for asynchronous processing (equivalent to OpenAI's background mode).
	/// </summary>
	public async Task<LLMClientResponse> CallAsync(
		string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
	{
		if (request.UseBackgroundMode)
			return await CallBatchBackgroundAsync(model, provider, request, ct);

		return await CallMessagesAsync(model, provider, request, ct);
	}

	private async Task<LLMClientResponse> CallMessagesAsync(
		string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
	{
		var url = BuildMessagesUrl(provider.Url);
		var auth = await ResolveAuthAsync(provider, ct);
		var prompt = BuildPrompt(request.Prompt, request.StructuredOutputSchema);
		byte[] payload = BuildMessagesPayload(model, prompt, request.Temperature, request.Tools, request.Reasoning, request.MaxOutputTokens);

		using var req = HttpRequestHelper.CreateJsonPost(url, payload);
		ApplyHeaders(req, auth);

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!resp.IsSuccessStatusCode)
		{
			var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
			throw new HttpRequestException(
				$"Anthropic chat call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
		}

		await using var stream = await resp.Content.ReadAsStreamAsync(ct);
		using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
		var root = json.RootElement;

		return BuildResponse(root, request, model);
	}

	private async Task<LLMClientResponse> CallBatchBackgroundAsync(
		string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
	{
		var batchUrl = BuildBatchesUrl(provider.Url);
		var auth = await ResolveAuthAsync(provider, ct);
		var prompt = BuildPrompt(request.Prompt, request.StructuredOutputSchema);

		byte[] payload = BuildBatchPayload(model, prompt, request.Temperature, request.Tools, request.Reasoning, request.MaxOutputTokens);

		using var createReq = HttpRequestHelper.CreateJsonPost(batchUrl, payload);
		ApplyHeaders(createReq, auth);

		using var createResp = await _http.SendAsync(createReq, HttpCompletionOption.ResponseHeadersRead, ct);
		var createBody = await createResp.Content.ReadAsStringAsync(ct);

		if (!createResp.IsSuccessStatusCode)
		{
			// If batches API is not available, fall back to synchronous
			if (IsBatchUnsupported(createResp.StatusCode, createBody))
			{
				_logger.LogDebug("Anthropic batch API not available, falling back to synchronous call.");
				return await CallMessagesAsync(model, provider, request, ct);
			}

			throw new HttpRequestException(
				$"Anthropic batch creation failed: {(int)createResp.StatusCode} {createResp.ReasonPhrase ?? ""} - {createBody}");
		}

		return await PollBatchUntilCompleteAsync(batchUrl, auth, createBody, request, model, ct);
	}

	private async Task<LLMClientResponse> PollBatchUntilCompleteAsync(
		string batchUrl, AnthropicAuth auth, string responseBody,
		LLMClientRequest request, string model, CancellationToken ct)
	{
		var delay = BackgroundInitialPollDelay;

		while (true)
		{
			using var json = JsonDocument.Parse(responseBody);
			var root = json.RootElement;

			var status = TryGetString(root, "processing_status");

			if (string.Equals(status, "ended", StringComparison.OrdinalIgnoreCase))
			{
				// Retrieve results from the results_url
				var resultsUrl = TryGetString(root, "results_url");
				if (!string.IsNullOrWhiteSpace(resultsUrl))
					return await FetchBatchResultAsync(resultsUrl, auth, request, model, ct);

				// Fallback: try to get results from the batch response directly
				throw new HttpRequestException(
					$"Anthropic batch ended but no results_url found: {responseBody}");
			}

			if (IsTerminalBatchStatus(status))
				throw new HttpRequestException(
					$"Anthropic batch ended with status '{status}': {responseBody}");

			var batchId = TryGetString(root, "id");
			if (string.IsNullOrWhiteSpace(batchId))
				throw new HttpRequestException(
					$"Anthropic batch response did not include an id: {responseBody}");

			await Task.Delay(delay, ct);
			if (delay < BackgroundMaxPollDelay)
				delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, BackgroundMaxPollDelay.TotalMilliseconds));

			// Poll batch status
			var pollUrl = batchUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(batchId);
			using var pollReq = HttpRequestHelper.CreateGet(pollUrl);
			ApplyHeaders(pollReq, auth);

			using var pollResp = await _http.SendAsync(pollReq, HttpCompletionOption.ResponseHeadersRead, ct);
			responseBody = await pollResp.Content.ReadAsStringAsync(ct);

			if (!pollResp.IsSuccessStatusCode)
				throw new HttpRequestException(
					$"Anthropic batch polling failed: {(int)pollResp.StatusCode} {pollResp.ReasonPhrase ?? ""} - {responseBody}");
		}
	}

	private async Task<LLMClientResponse> FetchBatchResultAsync(
		string resultsUrl, AnthropicAuth auth, LLMClientRequest request, string model, CancellationToken ct)
	{
		using var resultsReq = HttpRequestHelper.CreateGet(resultsUrl);
		ApplyHeaders(resultsReq, auth);

		using var resultsResp = await _http.SendAsync(resultsReq, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!resultsResp.IsSuccessStatusCode)
		{
			var errBody = await HttpRequestHelper.ReadErrorBodyAsync(resultsResp, ct);
			throw new HttpRequestException(
				$"Anthropic batch results fetch failed: {(int)resultsResp.StatusCode} - {errBody}");
		}

		// Results are JSONL — we only submitted one request, so take the first line
		var resultsBody = await resultsResp.Content.ReadAsStringAsync(ct);
		var firstLine = resultsBody.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
		if (string.IsNullOrWhiteSpace(firstLine))
			throw new HttpRequestException("Anthropic batch results were empty.");

		using var resultDoc = JsonDocument.Parse(firstLine);
		var resultRoot = resultDoc.RootElement;

		// The result has structure: { "custom_id": "...", "result": { "type": "succeeded", "message": { ...message response... } } }
		if (resultRoot.TryGetProperty("result", out var result)
			&& result.TryGetProperty("message", out var message))
		{
			return BuildResponse(message, request, model);
		}

		// Check for error
		if (resultRoot.TryGetProperty("result", out var errResult)
			&& errResult.TryGetProperty("type", out var errType)
			&& errType.GetString() != "succeeded")
		{
			var errorMsg = errResult.TryGetProperty("error", out var errObj)
				? errObj.GetRawText()
				: "unknown error";
			throw new HttpRequestException($"Anthropic batch request failed: {errorMsg}");
		}

		throw new HttpRequestException($"Anthropic batch result has unexpected structure: {firstLine}");
	}

	private LLMClientResponse BuildResponse(JsonElement root, LLMClientRequest request, string model)
	{
		var content = ExtractContent(root);
		var usage = ExtractUsage(root);
		var toolCalls = ParseToolCalls(root);

		JsonNode? jsonOutput = null;
		if (request.StructuredOutputSchema != null && !string.IsNullOrWhiteSpace(content))
		{
			try { jsonOutput = JsonNode.Parse(content); }
			catch (JsonException ex)
			{
				_logger.LogDebug(ex, "Anthropic structured output was not valid JSON for model '{Model}'.", model);
			}
		}

		return new LLMClientResponse
		{
			Text = content,
			Json = jsonOutput,
			Usage = usage,
			Raw = JsonNode.Parse(root.GetRawText()),
			ToolCalls = toolCalls
		};
	}

	/// <summary>
	/// Lists the models exposed by the configured Anthropic-compatible endpoint.
	/// </summary>
	public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
	{
		var url = BuildModelsUrl(provider.Url);
		var auth = await ResolveAuthAsync(provider, ct);

		using var req = HttpRequestHelper.CreateGet(url);
		ApplyHeaders(req, auth);

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!resp.IsSuccessStatusCode)
		{
			var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
			throw new HttpRequestException(
				$"Anthropic model list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
		}

		await using var stream = await resp.Content.ReadAsStreamAsync(ct);
		using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
		return ParseModelResponse(json.RootElement);
	}

	internal static byte[] BuildMessagesPayload(
		string model,
		string prompt,
		double? temperature = null,
		IReadOnlyList<LLMToolDef>? tools = null,
		string? reasoning = null,
		int? maxOutputTokens = null)
	{
		const int DefaultMaxTokens = 16384;
		var thinkingBudget = NormalizeThinkingBudget(reasoning);
		var maxTokens = maxOutputTokens
			?? (thinkingBudget.HasValue ? Math.Max(DefaultMaxTokens, thinkingBudget.Value + 1024) : DefaultMaxTokens);

		using var ms = new MemoryStream();
		using (var w = new Utf8JsonWriter(ms))
		{
			w.WriteStartObject();
			w.WriteString("model", model);
			w.WriteNumber("max_tokens", maxTokens);

			if (temperature.HasValue && !thinkingBudget.HasValue)
				w.WriteNumber("temperature", temperature.Value);

			if (thinkingBudget.HasValue)
			{
				w.WriteStartObject("thinking");
				w.WriteString("type", "enabled");
				w.WriteNumber("budget_tokens", thinkingBudget.Value);
				w.WriteEndObject();
			}

			w.WriteStartArray("messages");
			w.WriteStartObject();
			w.WriteString("role", "user");
			w.WriteString("content", prompt);
			w.WriteEndObject();
			w.WriteEndArray();

			if (tools is { Count: > 0 })
			{
				w.WriteStartArray("tools");
				foreach (var tool in tools)
				{
					w.WriteStartObject();
					w.WriteString("name", tool.Name);
					if (!string.IsNullOrWhiteSpace(tool.Description))
						w.WriteString("description", tool.Description);
					w.WritePropertyName("input_schema");
					(tool.InputSchema ?? new JsonObject { ["type"] = "object" }).WriteTo(w);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			}

			w.WriteEndObject();
		}

		return ms.ToArray();
	}

	internal static string BuildMessagesUrl(string? baseUrl)
	{
		var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultEndpoint : baseUrl.TrimEnd('/');
		if (b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			return b;
		if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
			return b + "/messages";
		return b + "/v1/messages";
	}

	internal static string BuildBatchesUrl(string? baseUrl)
	{
		var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultEndpoint : baseUrl.TrimEnd('/');
		if (b.EndsWith("/batches", StringComparison.OrdinalIgnoreCase))
			return b;
		if (b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			return b + "/batches";
		if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
			return b + "/messages/batches";
		return b + "/v1/messages/batches";
	}

	internal static string BuildModelsUrl(string? baseUrl)
	{
		var b = string.IsNullOrWhiteSpace(baseUrl) ? DefaultEndpoint : baseUrl.TrimEnd('/');
		if (b.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
			return b;
		if (b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
			b = b[..^"/messages".Length];
		if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
			return b + "/models";
		return b + "/v1/models";
	}

	internal static int? NormalizeThinkingBudget(string? reasoning)
	{
		if (string.IsNullOrWhiteSpace(reasoning))
			return null;

		return reasoning.Trim().ToLowerInvariant() switch
		{
			"auto" or "none" or "off" or "false" or "0" => null,
			"minimal" or "min" or "low" => 1024,
			"medium" or "med" => 4096,
			"high" => 8192,
			"max" or "maximum" => 16000,
			_ => null
		};
	}

	/// <summary>
	/// Builds the payload for the Anthropic Message Batches API.
	/// Wraps a single message request in the batch format.
	/// </summary>
	internal static byte[] BuildBatchPayload(
		string model,
		string prompt,
		double? temperature = null,
		IReadOnlyList<LLMToolDef>? tools = null,
		string? reasoning = null,
		int? maxOutputTokens = null)
	{
		const int DefaultMaxTokens = 16384;
		var thinkingBudget = NormalizeThinkingBudget(reasoning);
		var maxTokens = maxOutputTokens
			?? (thinkingBudget.HasValue ? Math.Max(DefaultMaxTokens, thinkingBudget.Value + 1024) : DefaultMaxTokens);

		using var ms = new MemoryStream();
		using (var w = new Utf8JsonWriter(ms))
		{
			w.WriteStartObject();
			w.WriteStartArray("requests");

			// Single request in the batch
			w.WriteStartObject();
			w.WriteString("custom_id", "bg-request-1");

			// params = the standard messages API body
			w.WriteStartObject("params");
			w.WriteString("model", model);
			w.WriteNumber("max_tokens", maxTokens);

			if (temperature.HasValue && !thinkingBudget.HasValue)
				w.WriteNumber("temperature", temperature.Value);

			if (thinkingBudget.HasValue)
			{
				w.WriteStartObject("thinking");
				w.WriteString("type", "enabled");
				w.WriteNumber("budget_tokens", thinkingBudget.Value);
				w.WriteEndObject();
			}

			w.WriteStartArray("messages");
			w.WriteStartObject();
			w.WriteString("role", "user");
			w.WriteString("content", prompt);
			w.WriteEndObject();
			w.WriteEndArray();

			if (tools is { Count: > 0 })
			{
				w.WriteStartArray("tools");
				foreach (var tool in tools)
				{
					w.WriteStartObject();
					w.WriteString("name", tool.Name);
					if (!string.IsNullOrWhiteSpace(tool.Description))
						w.WriteString("description", tool.Description);
					w.WritePropertyName("input_schema");
					(tool.InputSchema ?? new JsonObject { ["type"] = "object" }).WriteTo(w);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			}

			w.WriteEndObject(); // end params
			w.WriteEndObject(); // end request

			w.WriteEndArray(); // end requests
			w.WriteEndObject();
		}

		return ms.ToArray();
	}

	private static bool IsBatchUnsupported(System.Net.HttpStatusCode statusCode, string body)
		=> statusCode is System.Net.HttpStatusCode.NotFound
			   or System.Net.HttpStatusCode.MethodNotAllowed
			   or System.Net.HttpStatusCode.NotImplemented
		   || ((int)statusCode == 400
			   && (body.Contains("batch", StringComparison.OrdinalIgnoreCase)
				   || body.Contains("unsupported", StringComparison.OrdinalIgnoreCase)));

	private static bool IsTerminalBatchStatus(string? status)
		=> status is not null
		   && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
			   || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
			   || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
			   || status.Equals("expired", StringComparison.OrdinalIgnoreCase));

	internal static string ExtractContent(JsonElement root)
	{
		if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
			return string.Empty;

		var parts = new List<string>();
		foreach (var item in content.EnumerateArray())
		{
			if (item.TryGetProperty("type", out var type)
				&& type.GetString() == "text"
				&& item.TryGetProperty("text", out var text)
				&& text.ValueKind == JsonValueKind.String)
			{
				var value = text.GetString();
				if (!string.IsNullOrWhiteSpace(value))
					parts.Add(value);
			}
		}

		return string.Join("", parts).Trim();
	}

	internal static List<ToolCallResult>? ParseToolCalls(JsonElement root)
	{
		if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
			return null;

		var results = new List<ToolCallResult>();
		foreach (var item in content.EnumerateArray())
		{
			if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_use")
				continue;

			var input = item.TryGetProperty("input", out var inputElement)
				? JsonNode.Parse(inputElement.GetRawText())
				: null;

			results.Add(new ToolCallResult
			{
				Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
				Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
				Arguments = input
			});
		}

		return results.Count == 0 ? null : results;
	}

	internal static JsonObject? ExtractUsage(JsonElement root)
		=> root.TryGetProperty("usage", out var usage) ? JsonNode.Parse(usage.GetRawText()) as JsonObject : null;

	internal static IReadOnlyList<LLMModelDescriptor> ParseModelResponse(JsonElement root)
	{
		IEnumerable<JsonElement> items = [];
		if (root.ValueKind == JsonValueKind.Array)
			items = root.EnumerateArray();
		else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
			items = data.EnumerateArray();

		var results = new List<LLMModelDescriptor>();
		foreach (var item in items)
		{
			var id = TryGetString(item, "id") ?? TryGetString(item, "model");
			if (string.IsNullOrWhiteSpace(id))
				continue;

			var displayName = TryGetString(item, "display_name") ?? TryGetString(item, "name") ?? id;
			results.Add(new LLMModelDescriptor(id, displayName, "anthropic", "anthropic"));
		}

		return results
			.GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
	}

	internal static string? ResolveApiKey(ModelProviderOptions provider)
	{
		if (!string.IsNullOrWhiteSpace(provider.ApiKey))
			return provider.ApiKey;

		return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
			   ?? Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
	}

	private async Task<AnthropicAuth> ResolveAuthAsync(ModelProviderOptions provider, CancellationToken ct)
	{
		var apiKey = ResolveApiKey(provider);
		if (!string.IsNullOrWhiteSpace(apiKey))
			return new AnthropicAuth(apiKey, null);

		if (HasOidcConfiguration(provider))
		{
			ValidateOidcConfiguration(provider);
			var tokenProvider = new OidcJwtApiKeyProvider(
				_http,
				new OidcClientCredentialsConfig(
					provider.Issuer!,
					provider.ClientId!,
					provider.Scopes!,
					provider.ClientSecret,
					provider.PrivateKeyPem));
			return new AnthropicAuth(null, await tokenProvider.GetApiKeyAsync(ct));
		}

		return new AnthropicAuth(null, null);
	}

	private static void ApplyHeaders(HttpRequestMessage req, AnthropicAuth auth)
	{
		req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
		if (!string.IsNullOrWhiteSpace(auth.ApiKey))
			req.Headers.TryAddWithoutValidation("x-api-key", auth.ApiKey);
		if (!string.IsNullOrWhiteSpace(auth.BearerToken))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerToken);
	}

	private static string BuildPrompt(string prompt, JsonNode? structuredOutputSchema)
	{
		if (structuredOutputSchema == null)
			return prompt;

		return $"""
{prompt}

Return only valid JSON matching this JSON Schema. Do not wrap it in Markdown fences and do not include explanatory text.
Schema:
{structuredOutputSchema.ToJsonString()}
""";
	}

	private static bool HasOidcConfiguration(ModelProviderOptions provider)
		=> !string.IsNullOrWhiteSpace(provider.Issuer)
		   || !string.IsNullOrWhiteSpace(provider.ClientId)
		   || !string.IsNullOrWhiteSpace(provider.Scopes)
		   || !string.IsNullOrWhiteSpace(provider.ClientSecret)
		   || !string.IsNullOrWhiteSpace(provider.PrivateKeyPem);

	private static void ValidateOidcConfiguration(ModelProviderOptions provider)
	{
		if (string.IsNullOrWhiteSpace(provider.Issuer))
			throw new InvalidOperationException("OIDC issuer is required when OIDC authentication is configured.");
		if (string.IsNullOrWhiteSpace(provider.ClientId))
			throw new InvalidOperationException("OIDC client_id is required when OIDC authentication is configured.");
		if (string.IsNullOrWhiteSpace(provider.Scopes))
			throw new InvalidOperationException("OIDC scopes are required when OIDC authentication is configured.");
		if (string.IsNullOrWhiteSpace(provider.ClientSecret)
			&& string.IsNullOrWhiteSpace(provider.PrivateKeyPem))
		{
			throw new InvalidOperationException("OIDC client_secret or private_key_pem is required when OIDC authentication is configured.");
		}
	}

	private static string? TryGetString(JsonElement element, string name)
		=> element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private sealed record AnthropicAuth(string? ApiKey, string? BearerToken);
}



