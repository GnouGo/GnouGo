
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Auth.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for Claude models through the Anthropic Messages API.
/// </summary>
public sealed class ClaudeLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
	public const string DefaultEndpoint = "https://api.anthropic.com/v1";
	private const string AnthropicVersion = "2023-06-01";

	private readonly HttpClient _http;
	private readonly ILogger<ClaudeLLMProvider> _logger;

	public ClaudeLLMProvider(HttpClient http, ILogger<ClaudeLLMProvider>? logger = null)
	{
		_http = http;
		_logger = logger ?? NullLogger<ClaudeLLMProvider>.Instance;
		LLMHttpClientDefaults.EnsureMinimumTimeout(_http);
	}

	/// <inheritdoc />
	public string ProviderType => "claude";

	/// <inheritdoc />
	public async Task<LLMClientResponse> CallAsync(
		string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
	{
		var url = BuildMessagesUrl(provider.Url);
		var auth = await ResolveAuthAsync(provider, ct);
		var prompt = BuildPrompt(request.Prompt, request.StructuredOutputSchema);
		byte[] payload = BuildMessagesPayload(model, prompt, request.Temperature, request.Tools, request.Reasoning);

		using var req = HttpRequestHelper.CreateJsonPost(url, payload);
		ApplyHeaders(req, auth);

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!resp.IsSuccessStatusCode)
		{
			var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
			throw new HttpRequestException(
				$"Claude chat call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
		}

		await using var stream = await resp.Content.ReadAsStreamAsync(ct);
		using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
		var root = json.RootElement;

		var content = ExtractContent(root);
		var usage = ExtractUsage(root);
		var toolCalls = ParseToolCalls(root);

		JsonNode? jsonOutput = null;
		if (request.StructuredOutputSchema != null && !string.IsNullOrWhiteSpace(content))
		{
			try { jsonOutput = JsonNode.Parse(content); }
			catch (JsonException ex)
			{
				_logger.LogDebug(ex, "Claude structured output was not valid JSON for model '{Model}'.", model);
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

	/// <inheritdoc />
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
				$"Claude model list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
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
		string? reasoning = null)
	{
		var thinkingBudget = NormalizeThinkingBudget(reasoning);
		var maxTokens = thinkingBudget.HasValue ? Math.Max(4096, thinkingBudget.Value + 1024) : 4096;

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
			results.Add(new LLMModelDescriptor(id, displayName, "claude", "anthropic"));
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

	private async Task<ClaudeAuth> ResolveAuthAsync(ModelProviderOptions provider, CancellationToken ct)
	{
		var apiKey = ResolveApiKey(provider);
		if (!string.IsNullOrWhiteSpace(apiKey))
			return new ClaudeAuth(apiKey, null);

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
			return new ClaudeAuth(null, await tokenProvider.GetApiKeyAsync(ct));
		}

		return new ClaudeAuth(null, null);
	}

	private static void ApplyHeaders(HttpRequestMessage req, ClaudeAuth auth)
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

	private sealed record ClaudeAuth(string? ApiKey, string? BearerToken);
}

