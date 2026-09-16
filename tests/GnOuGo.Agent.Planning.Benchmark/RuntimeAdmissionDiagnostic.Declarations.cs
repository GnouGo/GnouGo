using GnOuGo.Flow.Planning.Tests;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    private static JsonObject DeclarationFixture(string name)
    {
        using var stream = typeof(RuntimeAdmissionDiagnostic).Assembly.GetManifestResourceStream("GnOuGo.Agent.Planning.Benchmark.RuntimeEvidenceDeclarations.json")
            ?? throw new InvalidOperationException("Missing frozen declaration preconditions.");
        return JsonNode.Parse(stream)![name]!.AsObject();
    }

    private static string DeclarationFixtureHash(string name) => PlanningGraphCompiler.Fingerprint(
        PlanningGraphCompiler.Fingerprint(Ports(name)) + ":" + DeclarationFixture(name).ToJsonString());

    private static PlanningSnapshot NewState(string name, PlanningSnapshot source)
    {
        var state = new PlanningSnapshot { Request = new() { SessionId = Identity + ":" + name, TenantId = Tenant,
            Prompt = name == "local" ? ProgressiveScenarios.Simple : Mixed, Baseline = Ports(name), Options = source.Request.Options.DeepClone().AsObject(),
            MaxRepairsPerWorkflowGate = 5, Generation = new() { ReasoningProfile = new() { Routine = "low", Behavior = "low", SemanticReview = "low" } } } };
        state.Request.Options["llm_budget"]!["max_calls"] = RuntimeAdmissionDiagnosticRules.MaxCalls;
        return state;
    }

    private static JsonObject Preflight(string name, PlanningSnapshot source)
    {
        var state = NewState(name, source);
        var decisions = PlanningSourceDecisions.InterpretationDecisions(state);
        var pages = PlanningDecisionPages.PackedPageCount(state, decisions);
        RuntimeAdmissionDiagnosticRules.RequirePreflight(pages);
        InstallDeclarations(name, state);
        return new() { ["interpretationPages"] = pages, ["sourceDecisions"] = decisions.Length,
            ["modelRuntimeDecisionsRemoved"] = decisions.Count(d => d.Schema["properties"]!["runtime"] is null),
            ["declarationFixtureValidated"] = true };
    }

    // This installs only explicit diagnostic contract preconditions. It neither
    // reinterprets live runtime answers nor infers contract spans from keywords.
    private static void InstallDeclarations(string name, PlanningSnapshot state)
    {
        var fixture = DeclarationFixture(name);
        if (fixture["promptSha256"]!.ToString() != PlanningGraphCompiler.Fingerprint(state.Request.Prompt))
            throw new InvalidOperationException("The declaration fixture source changed.");
        state.Obligations.RemoveAll(o => o.Kind is "declaration_candidate" or "declaration_constraint" or "omission_default");
        var assignments = new List<PlanningDeclarationAssignment>();
        foreach (var fact in fixture["facts"]!.AsArray())
        {
            var start = fact!["start"]!.GetValue<int>(); var length = fact["length"]!.GetValue<int>();
            if (state.Request.Prompt.Substring(start, length) != fact["text"]!.ToString()) throw new InvalidOperationException("The frozen source coordinates changed.");
            var reference = SourceSpan(state, start, length);
            var kind = fact["kind"]!.ToString();
            var id = "fixture_declaration_" + PlanningGraphCompiler.Fingerprint(reference.Id + ":" + kind)[..24];
            var obligation = new PlanningObligation(id, [reference.Id], "business_decision", kind, true);
            state.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(state, obligation), Disposition = "preliminary" });
            string? literal = null;
            if (fact["defaultText"] is { } text)
                literal = PlanningReferences.Lexical(state, PlanningChoiceEvidence.Parent(state, reference.Id), state.Request.Prompt)
                    .Single(r => r.Start >= start && r.Start + r.Length <= start + length && PlanningChoiceEvidence.Text(state, r.Id) == text.ToString()).Id;
            assignments.Add(new(id, kind == "declaration_candidate" ? "same_as" : "modifier_of",
                PlanningDeclarations.CanonicalId(fact["name"]!.ToString(), "main", fact["direction"]!.ToString()), null, null,
                literal is null ? "unspecified" : "optional", literal));
        }
        PlanningDeclarations.Commit(state, assignments, PlanningDeclarations.EvidenceFingerprint(state));
        PlanningDeclarations.RequireCurrent(state);
        if (state.Declarations.Count != 3 || state.Declarations.Count(d => d.Direction == "input") != 2 ||
            !state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == (name == "local" ? "record" : "sourceId")).Required ||
            state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold") is not { Required: false } threshold ||
            PlanningDeclarations.Default(state, threshold)?.Number != 100 ||
            state.Declarations.Single(d => d.Direction == "output") is not { Required: true } output ||
            PlanningDeclarations.Name(state, output) != "classifiedResult" || output.ModifierReferences.Count != 2)
            throw new InvalidOperationException("The supplied canonical declaration preconditions are incomplete.");
    }

    private static PlanningReference SourceSpan(PlanningSnapshot state, int start, int length)
    {
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request" && s.Clause.Start <= start && s.Clause.Start + s.Clause.Length >= start + length))
        foreach (var from in scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray())
        foreach (var to in scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray())
        {
            if (int.Parse(to!.ToString()[1..], System.Globalization.CultureInfo.InvariantCulture) <= int.Parse(from!.ToString()[1..], System.Globalization.CultureInfo.InvariantCulture)) continue;
            var reference = scope.Select(from.ToString(), to.ToString());
            if (reference.Start != start || reference.Length != length) continue;
            if (!state.References.Contains(reference)) state.References.Add(reference); return reference;
        }
        throw new InvalidOperationException("An exact frozen source span is unavailable.");
    }

    private static async Task SelfcheckAsync()
    {
        var source = new PlanningSnapshot(); source.Request.Options["llm_budget"] = new JsonObject { ["max_calls"] = 8 };
        var cases = new JsonArray();
        foreach (var name in RuntimeAdmissionDiagnosticRules.Cases)
        {
            var preflight = Preflight(name, source); var state = NewState(name, source);
            InstallDeclarations(name, state);
            // Synthetic evidence tests the supplied attachment coverage, not live interpretation.
            foreach (var input in PlanningIntentAssessment.IntentSources(state))
            foreach (var reference in PlanningReferences.Register(state, input.Id, input.Kind, input.Text).ToArray().Where(r => !string.IsNullOrWhiteSpace(input.Text.Substring(r.Start, r.Length))))
                state.RuntimeEvidence.Add(input.Authority == PlanningSourceAuthority.ConstraintsOnly ? PlanningOperations.PolicyEvidence(state, reference)
                    : PlanningOperations.SealRuntime(state, new("", reference.Id, PlanningChoiceEvidence.Parent(state, reference.Id).Id,
                        "contract", null, null, null, null, null, null, null, PlanningOperationNecessity.Unspecified, "")));
            var actionText = name == "local" ? "classifying a single record." : "Classify the loaded record: rejected when approved is false, high when approved is true and amount>=threshold, standard otherwise.";
            Add(actionText, "local_processing");
            if (name == "mixed") Add("Read the record identified by sourceId once from the external record store.", "external_read");
            Add(name == "local" ? "Preserve the original id and amount." : "preserve the loaded record's original id and amount.", "local_processing");
            state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
            var readIds = PlanningOperations.Scopes(state).Where(s => s.Evidence!.Kind == "external_read")
                .Select(s => PlanningOperations.EffectDomain(state, s.Evidence!).Single(p => p.Value.BoundaryReference == s.Evidence!.ActionReference).Key).ToArray();
            OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope,
                inputs: state.Declarations.Where(d => d.Direction == "input").Select(d => d.Id)),
                dependency: (producer, consumer) => name == "mixed" && readIds.Contains(producer) && !readIds.Contains(consumer));
            await PlanningOperations.ResolveAsync(state, new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask), CancellationToken.None);
            PlanningOperations.RequireExecutableIntent(state);
            if (state.Obligations.Count(PlanningSourceDecisions.IsOperation) != (name == "local" ? 1 : 2) ||
                PlanningOperations.DeclarationExclusions(state).Count != 1 || state.RequestAccounting.Count != 0)
                throw new InvalidOperationException("Diagnostic fixture coverage did not converge without model calls.");
            cases.Add((JsonNode)new JsonObject { ["case"] = name, ["passed"] = true, ["preflight"] = preflight, ["modelCalls"] = 0 });
            void Add(string text, string kind)
            {
                var action = SourceSpan(state, state.Request.Prompt.IndexOf(text, StringComparison.Ordinal), text.Length);
                var clause = PlanningChoiceEvidence.Parent(state, action.Id);
                state.RuntimeEvidence.Add(PlanningOperations.SealRuntime(state, new("", action.Id, clause.Id,
                    kind == "local_processing" ? "local_behavior" : "runtime_action", action.Id, null, action.Id, kind, "action", null, null, PlanningOperationNecessity.Unspecified, "")));
            }
        }
        Console.WriteLine(new JsonObject { ["evidence"] = "synthetic fixture selfcheck", ["cases"] = cases }.ToJsonString());
    }
}
