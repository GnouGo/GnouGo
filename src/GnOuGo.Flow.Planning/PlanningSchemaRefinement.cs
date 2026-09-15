using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Intersects requirements at unresolved coordinates without replacing established fragments.</summary>
internal static class PlanningSchemaRefinement
{
    internal sealed record Requirement(PlanningSchema Schema, IReadOnlyList<string> Members);
    internal static PlanningSchema Resolve(PlanningSchema prior, IReadOnlyList<Requirement> requirements, PlanningPreparation preparation)
    {
        var candidate = Clone(prior);
        foreach (var requirement in requirements.OrderByDescending(r => r.Members.Count))
            candidate = Merge(candidate, requirement.Schema, requirement.Members, 0);
        Preserve(prior, candidate);
        foreach (var requirement in requirements)
        {
            var selected = candidate;
            foreach (var member in requirement.Members)
                selected = selected.Properties.Single(p => p.Name == member && p.Required).Schema;
            if (!Fits(selected, requirement.Schema)) throw new InvalidOperationException("Consumer contracts conflict at this unresolved producer field.");
        }
        return candidate;

        bool Fits(PlanningSchema source, PlanningSchema destination)
        {
            try { return PlanningContractCompatibility.Fits(PlanningGraphCompiler.ToJsonSchema(source, preparation), PlanningGraphCompiler.ToJsonSchema(destination, preparation)); }
            catch (InvalidOperationException) { return false; }
        }
        PlanningSchema Merge(PlanningSchema current, PlanningSchema required, IReadOnlyList<string> members, int index)
        {
            if (index < members.Count)
            {
                if (current.Type == PlanningGraphSkeleton.Unresolved) current = new() { Type = "object" };
                if (current.Type != "object" || current.CapabilityId is not null) throw new InvalidOperationException("A projected contract requires an unresolved object member.");
                var field = current.Properties.SingleOrDefault(p => p.Name == members[index]);
                if (field is null) { field = new() { Name = members[index], Required = true, Schema = new() { Type = PlanningGraphSkeleton.Unresolved } }; current.Properties.Add(field); }
                field.Schema = Merge(field.Schema, required, members, index + 1); field.Required = true;
                return current;
            }
            if (current.Type == PlanningGraphSkeleton.Unresolved) return Clone(required);
            if (Fits(current, required)) return current;
            if (Fits(required, current)) return Clone(required);
            if (current.CapabilityId is not null || required.CapabilityId is not null || current.Type != required.Type)
                throw new InvalidOperationException("Consumer contracts require incompatible schema fragments.");
            if (current.Type == "array" && required.Items is not null)
            { current.Items = Merge(current.Items ?? new() { Type = PlanningGraphSkeleton.Unresolved }, required.Items, [], 0); return current; }
            if (current.Type == "object")
            {
                foreach (var field in required.Properties)
                {
                    var existing = current.Properties.SingleOrDefault(p => p.Name == field.Name);
                    if (existing is null) current.Properties.Add(new() { Name = field.Name, Required = field.Required, Schema = Clone(field.Schema), Default = field.Default });
                    else { existing.Schema = Merge(existing.Schema, field.Schema, [], 0); existing.Required |= field.Required; }
                }
                return current;
            }
            throw new InvalidOperationException("Consumer contracts require incompatible schema constraints.");
        }
        void Preserve(PlanningSchema locked, PlanningSchema result)
        {
            if (locked.Type == PlanningGraphSkeleton.Unresolved) return;
            if (locked.CapabilityId is not null)
            { if (!JsonSerializer.Serialize(locked, PlanningJsonContext.Default.PlanningSchema).Equals(JsonSerializer.Serialize(result, PlanningJsonContext.Default.PlanningSchema), StringComparison.Ordinal)) throw new InvalidOperationException("An established contract reference cannot change."); return; }
            if (locked.Type != result.Type || locked.Nullable != result.Nullable || !locked.Enum.SequenceEqual(result.Enum, StringComparer.Ordinal))
                throw new InvalidOperationException("Consumer constraints conflict with an established schema fragment.");
            if (locked.Items is not null && result.Items is not null) Preserve(locked.Items, result.Items);
            if (locked.Type == "object")
            {
                if (!locked.Properties.Select(p => (p.Name, p.Required)).SequenceEqual(result.Properties.Select(p => (p.Name, p.Required))))
                    throw new InvalidOperationException("Consumer constraints cannot rewrite an established object layout.");
                foreach (var field in locked.Properties)
                {
                    var retained = result.Properties.Single(p => p.Name == field.Name);
                    if (JsonSerializer.Serialize(field.Default, PlanningJsonContext.Default.PlanningValue) != JsonSerializer.Serialize(retained.Default, PlanningJsonContext.Default.PlanningValue))
                        throw new InvalidOperationException("Consumer constraints cannot rewrite an established member default.");
                    Preserve(field.Schema, retained.Schema);
                }
                if ((locked.AdditionalProperties is null) != (result.AdditionalProperties is null))
                    throw new InvalidOperationException("Consumer constraints cannot rewrite an established additional-member contract.");
                if (locked.AdditionalProperties is not null) Preserve(locked.AdditionalProperties, result.AdditionalProperties!);
            }
        }
    }
    private static PlanningSchema Clone(PlanningSchema schema) => JsonSerializer.Deserialize(JsonSerializer.Serialize(schema, PlanningJsonContext.Default.PlanningSchema), PlanningJsonContext.Default.PlanningSchema)!;
}
