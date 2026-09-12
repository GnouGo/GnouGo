using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageAssessment;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityMatchAssessment;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityCoverageContext
{

    internal static string BuildCapabilityCoverageCard(
        CapabilityCatalogEntry entry, CapabilityCatalog catalog)
    {
        var contracts = new JsonObject { ["input"] = entry.InputContract?.DeepClone(), ["output"] = entry.OutputContract?.DeepClone() };
        var declared = contracts.Any(p => p.Value is not null) ? "\nDeclared contracts:\n" + PlanningPromptContext.Json(contracts) : "";
        if (entry.RequestBindings.Count == 0)
            return entry.Card + declared;
        var baseEntry = catalog.Entries.FirstOrDefault(candidate =>
            candidate.RequestBindings.Count == 0
            && string.Equals(candidate.Resolution, entry.Resolution, StringComparison.Ordinal)
            && string.Equals(candidate.Server, entry.Server, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, entry.Kind, StringComparison.Ordinal)
            && string.Equals(candidate.Method, entry.Method, StringComparison.Ordinal));
        return (baseEntry is null ? entry.Card : baseEntry.Card + Environment.NewLine + entry.Card) + declared;
    }


}
