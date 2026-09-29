using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

// Transient ownership calculations; authoritative schemas and receipts stay intact.
internal static class PlanningCapabilityArguments
{
    private sealed record Binding(string[] Path, JsonNode? Value);

    private static List<Binding> Bindings(PlanningCapability capability)
    {
        var result = new List<Binding>();
        if (capability.StepType == "mcp.call")
        {
            if (capability.FixedInput.TryGetPropertyValue("request", out var request)) result.Add(new([], request));
            foreach (var binding in capability.RequestBindings)
            {
                if (string.IsNullOrEmpty(binding.Path) || !binding.Path.StartsWith("/", StringComparison.Ordinal)) throw new InvalidOperationException();
                var parts = binding.Path[1..].Split('/');
                foreach (var part in parts)
                    for (var i = 0; i < part.Length; i++)
                        if (part[i] == '~' && (++i == part.Length || part[i] is not ('0' or '1'))) throw new InvalidOperationException();
                result.Add(new(parts.Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray(), binding.Value));
            }
        }
        else
        {
            if (capability.RequestBindings.Count > 0) throw new InvalidOperationException();
            result.AddRange(capability.FixedInput.Select(p => new Binding([p.Key], p.Value)));
        }
        if (result.Where((a, i) => result.Skip(i + 1).Any(b => Prefix(a.Path, b.Path) || Prefix(b.Path, a.Path))).Any())
            throw new InvalidOperationException();
        return result;
    }

    private static bool Prefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path) => prefix.Count <= path.Count &&
        prefix.Select((part, index) => part == path[index]).All(equal => equal);

    internal static IReadOnlyList<PlanningDiagnostic> Validate(PlanningCapability capability)
    {
        try
        {
            foreach (var binding in Bindings(capability))
            {
                JsonNode? schema = capability.InputSchema;
                foreach (var part in binding.Path) schema = schema?["properties"]?[part];
                if (schema is not JsonObject contract || PlanningContractValidation.ValidateInstance(binding.Value, contract).Count > 0 ||
                    Interpolation(binding.Value)) throw new InvalidOperationException();
            }
            return [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return [new("CATALOG_BINDING_INVALID", "/operations/" + TaskOperations.Describe(capability).Id,
                "Catalog-owned bindings must have unambiguous declared paths and valid literal values. Refresh the capability contract; model repair cannot change it.")];
        }
        static bool Interpolation(JsonNode? node) => node switch
        {
            JsonObject obj => obj.Any(p => Interpolation(p.Value)), JsonArray array => array.Any(Interpolation),
            JsonValue value when value.TryGetValue<string>(out var text) => text.Contains("${", StringComparison.Ordinal), _ => false
        };
    }

    internal static bool Owns(PlanningCapability capability, IReadOnlyList<string> path) => Bindings(capability).Any(b => Prefix(b.Path, path));

    internal static PlanningOperation Editable(PlanningCapability capability)
    {
        var operation = TaskOperations.Describe(capability);
        var bindings = Bindings(capability);
        return new() { Id = operation.Id, Version = operation.Version, Description = operation.Description, Outputs = operation.Outputs,
            Inputs = operation.Inputs.Where(p => !bindings.Any(b => Prefix(b.Path, p.Path))).Select(port =>
            {
                var schema = port.Schema.DeepClone().AsObject();
                foreach (var binding in bindings.Where(b => Prefix(port.Path, b.Path))) Remove(schema, binding.Path[port.Path.Count..]);
                return new OperationPort { Name = port.Name, Path = port.Path.ToList(), Schema = schema,
                    Required = port.Required && !Supplies(capability, port, bindings) };
            }).ToList() };
    }

    private static void Remove(JsonObject schema, IReadOnlyList<string> path)
    {
        if (schema["properties"] is not JsonObject properties || properties[path[0]] is not JsonObject child) throw new InvalidOperationException();
        if (path.Count == 1)
        {
            properties.Remove(path[0]);
            if (schema["required"] is JsonArray required)
                for (var i = required.Count - 1; i >= 0; i--) if (required[i]?.ToString() == path[0]) required.RemoveAt(i);
        }
        else Remove(child, path.Skip(1).ToArray());
    }

    private static bool Supplies(PlanningCapability capability, OperationPort port, List<Binding> bindings)
    {
        if (!bindings.Any(b => Prefix(port.Path, b.Path))) return false;
        var input = Apply(new() { Kind = "object" }, capability);
        foreach (var part in port.Path)
        {
            var member = input.Members.SingleOrDefault(m => m.Name == part);
            if (member is null) return false;
            input = member.Value;
        }
        return PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(input), port.Schema).Count == 0;
    }

    internal static bool Assignment(PlanningCapability capability, string name, TaskValue value)
    {
        var port = TaskOperations.Describe(capability).Inputs.SingleOrDefault(p => p.Name == name);
        if (port is null) return false;
        return Bindings(capability).Any(b => Prefix(b.Path, port.Path) || Prefix(port.Path, b.Path) && Crosses(value, b.Path[port.Path.Count..]));
        static bool Crosses(TaskValue value, IReadOnlyList<string> path)
        {
            if (path.Count == 0 || value.Kind != "object") return true;
            var members = value.Members.Where(m => m.Name == path[0]).ToArray();
            return members.Length > 1 || members.Length == 1 && Crosses(members[0].Value, path.Skip(1).ToArray());
        }
    }

    internal static bool RemovalOnly(PlanningCapability capability, string name, TaskValue original, TaskValue candidate)
    {
        var copy = WithoutOwned(capability, name, original);
        return copy is not null && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(copy, PlanningJsonContext.Default.TaskValue),
            JsonSerializer.SerializeToNode(candidate, PlanningJsonContext.Default.TaskValue));
    }

    internal static TaskValue? WithoutOwned(PlanningCapability capability, string name, TaskValue original)
    {
        var port = TaskOperations.Describe(capability).Inputs.Single(p => p.Name == name);
        if (Owns(capability, port.Path)) return null; // Remove the binding, never replace its value.
        var copy = JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.TaskValue)!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
        foreach (var binding in Bindings(capability).Where(b => Prefix(port.Path, b.Path)))
        {
            var path = binding.Path[port.Path.Count..]; var value = copy;
            foreach (var part in path[..^1])
            {
                if (value.Kind != "object" || value.Members.Count(m => m.Name == part) > 1) return null;
                value = value.Members.SingleOrDefault(m => m.Name == part)?.Value!;
                if (value is null) break;
            }
            if (value is null) continue;
            if (value.Kind != "object" || value.Members.Count(m => m.Name == path[^1]) > 1) return null;
            value.Members.RemoveAll(m => m.Name == path[^1]);
        }
        return copy;
    }

    internal static PlanningValue Apply(PlanningValue input, PlanningCapability capability)
    {
        foreach (var binding in Bindings(capability))
        {
            if (binding.Path.Length == 0)
            {
                if (input.Kind != "object" || input.Members.Count != 0) throw new InvalidOperationException("A generated input overlaps a catalog-owned binding.");
                input = PlanningJsonTransport.Literal(binding.Value); continue;
            }
            var current = input;
            foreach (var part in binding.Path[..^1])
            {
                if (current.Kind != "object") throw new InvalidOperationException("A generated input overlaps a catalog-owned binding.");
                var member = current.Members.SingleOrDefault(m => m.Name == part);
                if (member is null) { member = new(part, new() { Kind = "object" }); current.Members.Add(member); }
                current = member.Value;
            }
            if (current.Kind != "object" || current.Members.Any(m => m.Name == binding.Path[^1]))
                throw new InvalidOperationException("A generated input overlaps a catalog-owned binding.");
            current.Members.Add(new(binding.Path[^1], PlanningJsonTransport.Literal(binding.Value)));
        }
        return input;
    }

    // The same owned values participate in complete-request type checking, without
    // changing the authoritative contract or accepting a model-owned override.
    internal static JsonObject EffectiveSchema(PlanningCapability capability, IEnumerable<(OperationPort Port, JsonObject Schema)> inputs)
    {
        var result = Object();
        foreach (var (port, schema) in inputs) Insert(port.Path, schema);
        foreach (var binding in Bindings(capability)) Insert(binding.Path, new() { ["const"] = binding.Value?.DeepClone() });
        return result;

        void Insert(IReadOnlyList<string> path, JsonObject schema)
        {
            if (path.Count == 0) { result = schema.DeepClone().AsObject(); return; }
            var current = result;
            foreach (var part in path.SkipLast(1))
            {
                Expand(current);
                var properties = current["properties"] as JsonObject ?? throw new InvalidOperationException("Input mappings overlap a non-object binding.");
                if (properties[part] is null) { properties[part] = Object(); current["required"]!.AsArray().Add((JsonNode)JsonValue.Create(part)!); }
                current = properties[part]!.AsObject();
            }
            Expand(current);
            if (current["properties"] is not JsonObject target || target.ContainsKey(path[^1]))
                throw new InvalidOperationException("Input mappings must be disjoint object bindings.");
            current["properties"]![path[^1]] = schema.DeepClone();
            if (!current["required"]!.AsArray().Any(n => n?.ToString() == path[^1])) current["required"]!.AsArray().Add((JsonNode)JsonValue.Create(path[^1])!);
        }
        static void Expand(JsonObject current)
        {
            if (current["const"] is not JsonObject literal) return;
            var fields = literal.Select(p => (p.Key, Value: p.Value?.DeepClone())).ToArray();
            current.Clear(); current["type"] = "object"; current["properties"] = new JsonObject();
            current["required"] = new JsonArray(); current["additionalProperties"] = false;
            foreach (var field in fields)
            { current["properties"]![field.Key] = new JsonObject { ["const"] = field.Value }; current["required"]!.AsArray().Add((JsonNode)JsonValue.Create(field.Key)!); }
        }
        static JsonObject Object() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray(), ["additionalProperties"] = false };
    }
}
