using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningBusinessJudge
{
    private const int MaxReferenceDiagnostics = 16;
    private const int MaxDiagnosticReferenceLength = 256;

    // Retain only a bounded rejection description, not the unusable model report
    // or values next to its proposed context locations. A null JSON value is an
    // existing location; it is distinct from a missing property or array index.
    internal static JsonArray ReferenceDiagnostics(JsonNode report, JsonObject context, out int omitted)
    {
        var errors = new JsonArray();
        var count = 0;
        var findings = report["findings"]!.AsArray();
        if (report["uncertainty"]!.ToString().Length > 8192) Add("/uncertainty", null, "text_limit_exceeded");
        if (findings.Count > 32) Add("/findings", null, "finding_limit_exceeded");
        else for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i]!;
            var prefix = "/findings/" + i.ToString(CultureInfo.InvariantCulture);
            Check(prefix + "/location", finding["location"]!.ToString());
            if (finding["explanation"]!.ToString().Length is 0 or > 8192)
                Add(prefix + "/explanation", null, "explanation_length_invalid");
            if (finding["uncertainty"]!.ToString().Length > 8192)
                Add(prefix + "/uncertainty", null, "text_limit_exceeded");
            var evidence = finding["evidence"]!.AsArray();
            if (evidence.Count is 0 or > 16) Add(prefix + "/evidence", null, "evidence_count_invalid");
            else for (var j = 0; j < evidence.Count; j++)
                Check(prefix + "/evidence/" + j.ToString(CultureInfo.InvariantCulture), evidence[j]!.ToString());
        }
        omitted = Math.Max(0, count - MaxReferenceDiagnostics);
        return errors;

        void Check(string location, string reference)
        {
            _ = Pointer(context, reference, out var exists, out var reason);
            if (!exists) Add(location, reference, reason!);
        }
        void Add(string location, string? reference, string reason)
        {
            count++;
            if (errors.Count < MaxReferenceDiagnostics) errors.Add((JsonNode)ReferenceDiagnostic(location, reference, reason));
        }
    }

    private static JsonObject ReferenceDiagnostic(string location, string? reference, string reason) => new()
    {
        ["location"] = Bounded(location),
        ["location_truncated"] = location.Length > MaxDiagnosticReferenceLength,
        ["reference"] = reference is null ? null : Bounded(reference),
        ["reference_truncated"] = reference?.Length > MaxDiagnosticReferenceLength,
        ["reason"] = reason
    };

    private static string Bounded(string value)
    {
        if (value.Length <= MaxDiagnosticReferenceLength) return value;
        var length = char.IsHighSurrogate(value[MaxDiagnosticReferenceLength - 1])
            ? MaxDiagnosticReferenceLength - 1 : MaxDiagnosticReferenceLength;
        return value[..length];
    }

    private static void RetainRejection(JsonObject entry, JsonArray diagnostics, int omitted)
    {
        entry["validation_status"] = "invalid";
        entry["validation_diagnostics"] = diagnostics;
        entry["validation_diagnostics_omitted"] = omitted;
    }

    private static void AddReferenceReview(PlanningSession state, JsonObject entry)
    {
        if (entry["validation_diagnostics"] is not JsonArray errors) return;
        for (var i = 0; i < Math.Min(errors.Count, MaxReferenceDiagnostics); i++)
        {
            var error = errors[i]!;
            var reference = error["reference"]?.ToString();
            var detail = reference is null ? "" : "; rejected reference: " + reference +
                (error["reference_truncated"]?.ToString() == "true" ? " (diagnostic text truncated)" : "");
            var locationSuffix = error["location_truncated"]?.ToString() == "true" ? " (diagnostic location truncated)" : "";
            state.ValidationResults.Add(new("judge:reference:" + i.ToString(CultureInfo.InvariantCulture), "inconclusive",
                "Business judge response at " + error["location"] + locationSuffix + detail + "; reason: " + ReferenceReason(error["reason"]!.ToString()) +
                ". The whole report is unusable; no edit was authorized and no retry was created.", []));
        }
        var omitted = entry["validation_diagnostics_omitted"]?.GetValue<int>() ?? 0;
        if (omitted > 0)
            state.ValidationResults.Add(new("judge:reference:omitted", "inconclusive",
                omitted.ToString(CultureInfo.InvariantCulture) + " additional response validation diagnostics were omitted from the bounded review. The whole report remains inconclusive.", []));
    }

    private static string ReferenceReason(string reason) => reason switch
    {
        "reference_not_found" => "the referenced context location does not exist",
        "invalid_pointer_escape" => "the JSON Pointer contains an invalid escape",
        "invalid_pointer_syntax" => "the reference must be a JSON Pointer into the supplied context",
        "pointer_too_long" => "the reference exceeds the 2,048-character validation limit",
        "finding_limit_exceeded" => "the report exceeds the 32-finding validation limit",
        "evidence_count_invalid" => "a finding must cite between one and 16 evidence references",
        "explanation_length_invalid" => "the explanation must contain between one and 8,192 characters",
        "text_limit_exceeded" => "the uncertainty text exceeds the 8,192-character validation limit",
        "response_schema_invalid" => "the response does not satisfy its issued schema",
        "response_json_invalid" => "the response could not be read as JSON",
        _ => "the response could not be validated"
    };
}
