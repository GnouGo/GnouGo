using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningBindingIdentity
{
    internal static string Id(PlanningValue value) => "b_" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(
        new[] { value.Kind, value.Source ?? "", value.ResultChannel ?? "default" }.Concat(value.Path).ToArray(), PlanningJsonContext.Default.StringArray));
}
