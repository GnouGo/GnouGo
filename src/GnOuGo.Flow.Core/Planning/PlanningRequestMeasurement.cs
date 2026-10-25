using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Captured complete-request ceilings for compact construction. Lower limits always win.</summary>
public sealed class PlanningConstructionInputLimits
{
    public int Selection { get; set; } = 3_000;
    public int Draft { get; set; } = 8_000;
    public int Review { get; set; } = 4_000;
}

public static class PlanningConstructionInputPolicy
{
    public static bool IsCompact(PlanningRequest request) => request.Options["construction_version"]?.GetValue<int>() == 2;

    public static int EffectiveLimit(PlanningRequest request, string purpose)
        => PlanningConstructionPhasePolicies.IsFresh(request)
            ? PlanningConstructionPhasePolicies.Resolve(request, purpose).MaxInputTokens
            : LegacyLimit(request, purpose);

    internal static int LegacyLimit(PlanningRequest request, string purpose)
    {
        if (!IsCompact(request)) return request.Generation.MaxInputTokensPerRequest;
        var captured = request.Options["construction_input_limits"]?.Deserialize(PlanningJsonContext.Default.PlanningConstructionInputLimits)
            ?? new PlanningConstructionInputLimits();
        var (limit, maximum) = purpose switch
        {
            "construction_intent" or "construction_select" or "construction_discovery" => (captured.Selection, 3_000),
            "construction_draft" or "replan" => (captured.Draft, 8_000),
            "construction_local_judge" or "construction_intermediate_judge" => (captured.Review, 4_000),
            _ => throw new PlanningConflictException("The compact construction request has no recognized phase allowance.")
        };
        if (limit <= 0) throw new PlanningConflictException("The captured compact input allowance must be positive.");
        return Math.Min(request.Generation.MaxInputTokensPerRequest, Math.Min(limit, maximum));
    }

    /// <summary>The phase title is part of the immutable issued schema and request hash.</summary>
    public static int EffectiveLimit(PlanningRequest planning, LLMRequest request)
        => EffectiveLimit(planning, IsCompact(planning) ? request.StructuredOutputSchema?["title"]?.GetValue<string>() ?? "" : "planning");
}

/// <summary>Non-executable, content-free measurements of one exact issued or rejected request.</summary>
public sealed class PlanningRequestMeasurement
{
    public int Version { get; set; } = 1;
    public string Estimator { get; set; } = "serialized_utf8_div3_plus256_v1";
    public string RequestId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string SchemaHash { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineHash { get; set; }
    public List<PlanningRequestContractIdentity> Contracts { get; set; } = [];
    public string Phase { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; set; }
    public string Admission { get; set; } = "prepared";
    public int InputLimit { get; set; }
    public long CompleteRequestBytes { get; set; }
    public long EstimatedInputTokens { get; set; }
    public List<PlanningRequestComponentMeasurement> Components { get; set; } = [];
}

public sealed class PlanningRequestComponentMeasurement
{
    public string Component { get; set; } = "";
    public long SerializedBytes { get; set; }
    /// <summary>Independent component estimate. Rounding and request overhead are not billed token usage.</summary>
    public long EstimatedTokens { get; set; }
}

public sealed class PlanningRequestContractIdentity
{
    public string Id { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }
}

public static class PlanningRequestMeasurements
{
    public static PlanningRequestMeasurement Measure(LLMRequest request, string phase, int inputLimit, string? stepId = null)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(request, PlanningJsonContext.Default.LLMRequest);
        var parts = new Dictionary<string, long>(StringComparer.Ordinal);
        string? baselineHash = null;
        var contracts = new List<PlanningRequestContractIdentity>();
        void Add(string name, long bytes) => parts[name] = parts.GetValueOrDefault(name) + bytes;
        var prompt = request.Prompt ?? "";
        var contextStart = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (contextStart >= 0)
        {
            contextStart++;
            try
            {
                var json = Encoding.UTF8.GetBytes(prompt[contextStart..]);
                var reader = new Utf8JsonReader(json);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
                var measured = new List<(string Component, long Bytes)>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                    var start = reader.TokenStartIndex;
                    var propertyName = reader.GetString()!;
                    var component = Component(propertyName, phase);
                    if (!reader.Read()) throw new JsonException();
                    reader.Skip();
                    var raw = Encoding.UTF8.GetString(json.AsSpan((int)start, (int)(reader.BytesConsumed - start)));
                    var bytes = JsonEncodedText.Encode(raw).EncodedUtf8Bytes.Length;
                    if (propertyName == "step")
                    {
                        using var step = JsonDocument.Parse(raw[(raw.IndexOf(':') + 1)..]);
                        if (step.RootElement.ValueKind == JsonValueKind.Object && step.RootElement.TryGetProperty("objective", out var objective))
                        {
                            var objectiveBytes = JsonEncodedText.Encode(objective.GetRawText()).EncodedUtf8Bytes.Length;
                            measured.Add(("objective", objectiveBytes));
                            bytes -= objectiveBytes;
                        }
                    }
                    if (propertyName == "interfaces")
                    {
                        using var interfaces = JsonDocument.Parse(raw[(raw.IndexOf(':') + 1)..]);
                        foreach (var contract in ProducerContracts(interfaces.RootElement))
                        {
                            var contractBytes = JsonEncodedText.Encode(contract.GetRawText()).EncodedUtf8Bytes.Length;
                            measured.Add(("mcp_contracts", contractBytes));
                            bytes -= contractBytes;
                        }
                    }
                    measured.Add((component, bytes));
                }
                if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new JsonException();
                using var context = JsonDocument.Parse(json);
                if (context.RootElement.TryGetProperty("baseHash", out var baseline) && baseline.ValueKind == JsonValueKind.String) baselineHash = baseline.GetString();
                if (context.RootElement.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
                    foreach (var operation in operations.EnumerateArray())
                        if (operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            contracts.Add(new() { Id = id.GetString()!, Version = Text(operation, "version"), Hash = Text(operation, "hash") });
                if (context.RootElement.TryGetProperty("interfaces", out var producerInterfaces))
                    foreach (var contract in ProducerContracts(producerInterfaces))
                        if (contract.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var identity = new PlanningRequestContractIdentity
                                { Id = id.GetString()!, Version = Text(contract, "version"), Hash = Text(contract, "hash") };
                            if (!contracts.Any(existing => existing.Id == identity.Id && existing.Version == identity.Version && existing.Hash == identity.Hash))
                                contracts.Add(identity);
                        }
                Add("instructions", JsonEncodedText.Encode(prompt[..contextStart]).EncodedUtf8Bytes.Length);
                foreach (var part in measured) Add(part.Component, part.Bytes);
            }
            catch (JsonException) { Add("instructions", JsonEncodedText.Encode(prompt).EncodedUtf8Bytes.Length); }
        }
        else Add("instructions", JsonEncodedText.Encode(prompt).EncodedUtf8Bytes.Length);
        using var document = JsonDocument.Parse(serialized);
        var schemaHash = "";
        if (document.RootElement.TryGetProperty("structuredOutputSchema", out var schema))
        {
            Add("response_schema", Encoding.UTF8.GetByteCount(schema.GetRawText()));
            schemaHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.GetRawText())));
            if (baselineHash is null && phase == "construction_select" && schema.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty("baseHash", out var baselineSchema) && baselineSchema.ValueKind == JsonValueKind.Object &&
                baselineSchema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array &&
                values.GetArrayLength() == 1 && values[0].ValueKind == JsonValueKind.String)
                baselineHash = values[0].GetString();
        }
        var remainder = serialized.LongLength - parts.Values.Sum();
        if (remainder < 0) throw new InvalidOperationException("Request component measurements overlap.");
        Add("envelope", remainder);
        return new()
        {
            RequestId = request.ClientRequestId ?? "", RequestHash = Convert.ToHexStringLower(SHA256.HashData(serialized)),
            SchemaHash = schemaHash, BaselineHash = baselineHash, Contracts = contracts,
            Phase = phase, StepId = stepId, InputLimit = inputLimit, CompleteRequestBytes = serialized.LongLength,
            EstimatedInputTokens = (serialized.LongLength + 2) / 3 + 256,
            Components = parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new PlanningRequestComponentMeasurement
                { Component = p.Key, SerializedBytes = p.Value, EstimatedTokens = (p.Value + 2) / 3 + (p.Key == "envelope" ? 256 : 0) }).ToList()
        };

        static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    }

    private static IEnumerable<JsonElement> ProducerContracts(JsonElement interfaces)
    {
        if (interfaces.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in interfaces.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("producers", out var producers) && producers.ValueKind == JsonValueKind.Array)
                foreach (var producer in producers.EnumerateArray())
                    if (producer.ValueKind == JsonValueKind.Object && producer.TryGetProperty("contract", out var contract) && contract.ValueKind == JsonValueKind.Object)
                        yield return contract;
    }

    private static string Component(string key, string phase) => key switch
    {
        "request" or "continuation" or "originalContext" or "originalDirectory" => "intent",
        "objective" => "objective",
        "instructions" => "instructions",
        "sources" => phase is "construction_intent" or "construction_select" ? "intent" : "mcp_descriptors",
        "requirements" or "answers" or "globalConstraints" or "requirementsPage" => "requirements",
        "operations" or "contracts" => "mcp_contracts",
        "operationDirectory" or "optionalOperationContracts" or "capabilities" or "descriptors" or "describedOperations" or "providerRequests" => "mcp_descriptors",
        "interfaces" or "imports" or "inputDeclarations" or "scopeInterface" or "group" or "scopeGroup" or "existingInputs" or "existingOutputs" => "interfaces",
        "draft" or "tasks" or "fragment" or "closures" => "fragment",
        "evidence" => "evidence",
        "diagnostics" => "diagnostics",
        _ => "construction_context"
    };
}
