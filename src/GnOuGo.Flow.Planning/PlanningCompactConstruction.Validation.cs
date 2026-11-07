using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static bool CanReselectInitialDraft(PlanningSession state) =>
        state.Construction is { Candidate: null } construction &&
        construction.Compact is { CurrentStep.DiscoveryComplete: true } &&
        state.EditablePaths is null && state.Request.Options["automatic_correction"]?.ToString() != "false" &&
        PlanningModelCalls.HasDraftRepairCapacity(state, construction.DraftCorrections) &&
        PlanningModelCalls.HasRepairCapacity(state);

    internal static void Reselect(PlanningSession state, string reason)
    {
        var construction = state.Construction!; var compact = State(state); var step = compact.CurrentStep;
        if (step is null || construction.Candidate?.AuthorizedPaths is not null || state.EditablePaths is not null ||
            state.PendingCall is not null || string.IsNullOrWhiteSpace(reason) || !state.Diagnostics.Any(d => d.Required))
            PlanningConstruction.Fail("CONSTRUCTION_RESELECTION_INVALID", "/reselectStep", "Reselection requires a diagnosed tentative step and cannot expand explicit correction authority.");
        compact.RejectedSteps.Add(new JsonObject { ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
            ["candidateHash"] = construction.Candidate?.Hash, ["reason"] = reason,
            ["extension"] = construction.Candidate is { } candidate ? PlanningConstruction.Node(candidate.Extension, PlanningJsonContext.Default.PlanningConstructionExtension) : null,
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
            ["evidence"] = construction.Evidence?.DeepClone(),
            ["reviews"] = new JsonArray((construction.Candidate?.Reviews ?? PlanningConstruction.ReplacementDefectReviews(state))
                .Select(r => r.DeepClone()).ToArray()) });
        compact.ReplacementScope = step!.Scope; compact.ReplacementRequirements = step.RequirementIds.ToList();
        construction.DraftCorrections = Math.Max(construction.DraftCorrections, construction.Candidate?.Corrections ?? 0);
        construction.Candidate = null;
        compact.CurrentStep = null; compact.PendingCommitments.Clear();
        construction.PendingReviewContext = null; construction.PendingReviewHash = null; construction.Evidence = null; construction.EvidenceRequests.Clear();
    }

    internal static void StageCommitments(PlanningSession state, List<PlanningConstructionCommitment>? proposed,
        PlanningConstructionCandidate? candidate = null)
    {
        var compact = State(state); var step = compact.CurrentStep!;
        var operations = candidate is null ? step.OperationIds : new TaskPlanSymbols(candidate.Plan).Tasks.Values
            .Where(task => candidate.TaskIds.Contains(task.Task.Id, StringComparer.Ordinal)).Select(task => task.Task.Operation)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var next = step.RequirementIds.ToDictionary(id => id, _ => operations.Select(op => new PlanningCommitment(op)).ToList(), StringComparer.Ordinal);
        foreach (var item in proposed ?? [])
        {
            if (!step.RequirementIds.Contains(item.RequirementId, StringComparer.Ordinal) || !step.OperationIds.Contains(item.Operation, StringComparer.Ordinal) ||
                !operations.Contains(item.Operation, StringComparer.Ordinal))
                PlanningConstruction.Fail("CONSTRUCTION_COMMITMENT_INVALID", "/commitments", "Bind only this step's original requirements and selected exact operations.");
            if (item.Result is { } result && !step.OutputNames.Contains(result.Output, StringComparer.Ordinal))
                PlanningConstruction.Fail("CONSTRUCTION_COMMITMENT_INVALID", "/commitments", "A result commitment must name one of this step's explicitly selected public outputs.");
            var obligation = new PlanningCommitment(item.Operation) { Result = item.Result };
            var probe = new PlanningRequirements { Inputs = state.Requirements!.Inputs, Outputs = state.Requirements.Outputs,
                Outcomes = [new(item.RequirementId, state.Requirements.Outcomes.Single(r => r.Id == item.RequirementId).Description) { Commitments = [obligation] }] };
            var invalid = PlanningCommitments.Validate(state, probe, final: true);
            if (invalid.Count > 0) throw new PlanningResponseException(invalid);
            next[item.RequirementId].RemoveAll(c => c.Operation == item.Operation && c.Result is null);
            next[item.RequirementId].Add(obligation);
        }
        compact.PendingCommitments = next;
    }

    internal static void ValidateExtension(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var step = State(state).CurrentStep!;
        if (candidate.AuthorizedPaths is not null)
        {
            if (candidate.Extension.ContinueInScope is not null)
                PlanningConstruction.Fail("CONSTRUCTION_BLOCK_CONTINUATION_INVALID", "/extension/continueInScope",
                    "An explicit scoped revision cannot continue or expand the selected business block.");
            return; // The existing typed patch verifier owns this explicit revision boundary.
        }
        if (candidate.Reviews.Any(row => row["purpose"]?.ToString() == "construction_transition")) return;
        if (step.BlockId is null || CurrentBlock(state)?.Id != step.BlockId || candidate.Extension.CompleteBlock is null)
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_INVALID", "/extension/completeBlock", "Every new fragment belongs to the retained business block and explicitly states whether it claims business completion.");
        if (candidate.Extension.Complete && candidate.Extension.CompleteBlock != true)
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_INVALID", "/extension/completeBlock", "Workflow completion also requires the retained business block completion claim.");
        if (candidate.Extension.CompleteBlock == true && candidate.Extension.ContinueInScope is not null)
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_INVALID", "/extension/completeBlock", "An unfinished implementation continuation cannot complete the business block.");
        if (candidate.Extension.Scope != step.Scope ||
            candidate.Extension.RequirementIds.Any(id => !step.RequirementIds.Contains(id, StringComparer.Ordinal)) ||
            CandidateCompletesBlock(candidate) && step.CloseRequirementIds.Any(id => !candidate.Extension.RequirementIds.Contains(id, StringComparer.Ordinal)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/extension", "Keep the selected scope and requirements; a closing requirement must be associated with this fragment.");
        if (step.Kind == "close" && step.CloseScopes is { } selectedClosure &&
            (!candidate.Extension.CloseScopes.SequenceEqual(selectedClosure) ||
             candidate.Extension.Complete != selectedClosure.Contains("/root", StringComparer.Ordinal)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/extension/closeScopes", "Preserve the explicitly selected requirement-only or scope closure and its completion claim.");
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var kinds = BusinessFragmentKinds(state);
        var topLevel = candidate.Extension.Tasks.Concat(candidate.Extension.Always).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        if (candidate.Extension.Groups.Count > 0 && (step.Kind != "group" || BlockAnchorConstructed(state)) ||
            candidate.Extension.Choices.Count > 0 && step.Kind != "choice")
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/extension", "Create only the selected control or declaration boundary once within this business block.");
        if (candidate.TaskIds.Any(id => !kinds.Contains(symbols.Tasks[id].Task.Kind) &&
                !(SupportsBusinessFragment(step.Kind) && !topLevel.Contains(id) && NestedControlKinds.Contains(symbols.Tasks[id].Task.Kind, StringComparer.Ordinal)) ||
            symbols.Tasks[id].Task.Operation is { } operation && !step.OperationIds.Contains(operation, StringComparer.Ordinal) ||
            symbols.Tasks[id].Task.Kind == "call" && symbols.Tasks[id].Task.Group != step.GroupId))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/extension/tasks", "The extension exceeds the issued task-kind or operation selection.");
        ValidateEmptyOpening(state, candidate);
        var added = candidate.TaskIds.ToHashSet(StringComparer.Ordinal);
        var derivedImports = UsesDerivedImports(state, candidate);
        foreach (var task in candidate.TaskIds.Select(id => symbols.Tasks[id].Task))
        {
            if (!derivedImports && task.DependsOn.Any(id => !added.Contains(id) && !step.DependencyTaskIds.Contains(id, StringComparer.Ordinal)))
                PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", "/extension/tasks", "Select each accepted explicit dependency before drafting its consumer.");
            var path = "/tasks/" + task.Id;
            var scope = symbols.Tasks[task.Id].Scope.Path;
            foreach (var input in task.Inputs) CheckImport(input.Value, path + "/inputs/" + input.Name, scope);
            foreach (var output in task.Outputs) CheckImport(output.Value, path + "/outputs/" + output.Name, scope);
            if (task.Requires is not null) CheckImport(task.Requires, path + "/requires", scope);
            if (task.Condition is not null) CheckImport(task.Condition, path + "/condition", scope);
            if (task.Items is not null) CheckImport(task.Items, path + "/items", scope);
        }
        for (var i = 0; i < candidate.Extension.Outputs.Count; i++)
            CheckImport(candidate.Extension.Outputs[i].Value, "/extension/outputs/" + i + "/value", step.Scope);
        for (var i = 0; i < candidate.Extension.Choices.Count; i++)
            for (var j = 0; j < candidate.Extension.Choices[i].Alternatives.Count; j++)
                CheckImport(candidate.Extension.Choices[i].Alternatives[j].Value, "/extension/choices/" + i + "/alternatives/" + j + "/value", step.Scope);
        ValidateContinuationBinding(state, candidate);
        ValidateBoundaryTransition(state, candidate);
        ValidateBlockContinuation(state, candidate);

        void CheckImport(TaskValue value, string location, string scope)
        {
            // Fresh ordinary drafts are checked against actual semantic visibility.
            // Selected inputs remain context hints, not a second permission system.
            if (derivedImports) return;
            // A selected field is an exact typed import; its wider producer object is not implicitly imported.
            if (step.Inputs.Any(selected => JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(selected, PlanningJsonContext.Default.TaskValue),
                JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue)))) return;
            if (value.Kind is "input" or "item" or "index" ||
                value.Kind is "output" or "present" && !added.Contains(value.Source ?? "") ||
                value.Kind == "choice" && candidate.Extension.Choices.All(c => c.Id != value.Source))
            {
                {
                    var binding = new JsonObject { ["kind"] = value.Kind };
                    if (value.Source is not null) binding["source"] = value.Source;
                    if (value.Port is not null) binding["port"] = value.Port;
                    PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", location,
                        $"Binding {PlanningJsonTransport.Prompt(binding)} in scope '{scope}' is absent from step.inputs. Imports are fixed for this draft; return reselectStep alone to select this exact typed binding before using it.");
                }
                PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", "/extension", "An external binding was not selected as an exact typed import; reselect the tentative step with the required input.");
            }
            for (var i = 0; i < value.Items.Count; i++) CheckImport(value.Items[i], location + "/items/" + i, scope);
            for (var i = 0; i < value.Members.Count; i++) CheckImport(value.Members[i].Value, location + "/members/" + i + "/value", scope);
        }
    }

    internal static void RevisionStep(PlanningSession state, string objective)
    {
        var compact = State(state); var candidate = state.Construction!.Candidate!;
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var requirements = state.Construction.Checkpoints.Where(c => c.TaskIds.Any(candidate.TaskIds.Contains)).SelectMany(c => c.RequirementIds)
            .Distinct(StringComparer.Ordinal).ToList();
        if (requirements.Count == 0) requirements = state.Requirements!.Outcomes.Select(r => r.Id).ToList();
        compact.CurrentStep = new() { Id = "revision_" + (++compact.NextStep), BaseHash = candidate.BaseHash,
            Scope = candidate.Extension.Scope, Kind = "revision", Objective = objective, RequirementIds = requirements,
            CloseRequirementIds = requirements.Where(id => state.Construction.Requirements.Any(requirement =>
                requirement.Id == id && requirement.Status == "closed")).ToList(), DiscoveryComplete = true,
            OperationIds = candidate.TaskIds.Where(symbols.Tasks.ContainsKey).Select(id => symbols.Tasks[id].Task.Operation).OfType<string>().Distinct(StringComparer.Ordinal).ToList() };
        candidate.Extension.RequirementIds = requirements;
    }

    internal static PlanningRequirements CandidateRequirements(PlanningSession state)
    {
        var requirements = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            PlanningJsonContext.Default.PlanningRequirements)!;
        foreach (var (id, pending) in State(state).PendingCommitments)
        {
            var index = requirements.Outcomes.FindIndex(r => r.Id == id);
            if (index < 0) throw new PlanningConflictException("A tentative commitment lost its original requirement.");
            var original = requirements.Outcomes[index];
            var commitments = (original.Commitments ?? []).Concat(pending).DistinctBy(c => (c.Operation, c.Result?.Port, c.Result?.Output)).ToList();
            requirements.Outcomes[index] = original with { Commitments = commitments };
        }
        return requirements;
    }

    internal static IEnumerable<PlanningDiagnostic> CandidateFindings(PlanningSession state)
    {
        var compact = State(state); var candidate = state.Construction!.Candidate!; var step = compact.CurrentStep!;
        var tentative = CandidateRequirements(state);
        foreach (var diagnostic in PlanningCommitments.Validate(state, tentative, final: true)) yield return diagnostic;
        var selectedOperations = new TaskPlanSymbols(candidate.Plan).Tasks.Values.Where(t => candidate.TaskIds.Contains(t.Task.Id, StringComparer.Ordinal))
            .Select(t => t.Task.Operation).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var binding in compact.PendingCommitments.Values.SelectMany(c => c).DistinctBy(c => c.Operation))
            if (!selectedOperations.Contains(binding.Operation))
                yield return new(PlanningCommitments.MissingOperation, "/construction/candidate", "The selected business operation is absent from this draft: " + binding.Operation);
        if (CandidateCompletesBlock(candidate) && candidate.AuthorizedPaths is null)
        {
            var symbols = new TaskPlanSymbols(candidate.Plan);
            selectedOperations.UnionWith(BusinessBlockCheckpoints(state).SelectMany(checkpoint => checkpoint.TaskIds)
                .Where(symbols.Tasks.ContainsKey).Select(id => symbols.Tasks[id].Task.Operation).OfType<string>());
            foreach (var operation in step.OperationIds.Where(operation => !selectedOperations.Contains(operation)))
                yield return new(PlanningCommitments.MissingOperation, "/construction/candidate",
                    "The selected business operation is absent from this completed block: " + operation);
        }
        if (candidate.AuthorizedPaths is null && !candidate.Extension.Complete && CandidateCompletesBlock(candidate))
        {
            foreach (var id in step.CloseRequirementIds)
            {
                var sources = compact.RequirementSources.GetValueOrDefault(id) ?? [];
                var names = compact.Sources.Where(source => source.Indexed && source.Version == PlanningGraphCompiler.Fingerprint(source.Text) && sources.Contains(source.Id))
                    .SelectMany(source => source.Outputs ?? []).Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var output in (state.Requirements!.Outputs ?? []).Where(output => output.Required && names.Contains(output.Name)))
                    if (!candidate.Plan.Root.Outputs.Any(binding => binding.Name == output.Name))
                        yield return new("CONSTRUCTION_REQUIREMENT_OUTPUT_MISSING", "/step/closeRequirementIds",
                            $"Requirement '{id}' declares required public output '{output.Name}', but the tentative plan has no root binding for it. Keep that requirement open until its public result is bound; a local export does not bind a root output.");
            }
        }
        if (!candidate.Extension.Complete) yield break;
        if (compact.Sources.Any(s => s.Kind is "page" or "answer_page" or "revision_page" && !s.Indexed))
            yield return new("CONSTRUCTION_SOURCE_REMAINING", "/construction/compact/sources", "Every original source page must be indexed before completion.");
        foreach (var requirement in state.Construction.Requirements.Where(r => !step.CloseRequirementIds.Contains(r.Id, StringComparer.Ordinal) &&
            !RequirementCompletionClaimed(state, r.Id)))
            yield return new("CONSTRUCTION_REQUIREMENT_REMAINING", "/requirements/" + requirement.Id,
                "This original requirement has contributions but has not explicitly closed; continue its business substeps before completion.");
    }

    internal static void Accept(PlanningSession state, bool reviewed)
    {
        var compact = State(state); var step = compact.CurrentStep!;
        state.Requirements = CandidateRequirements(state);
        var continuation = state.Construction!.Candidate!.Extension.ContinueInScope;
        var complete = reviewed && state.Construction.Candidate.Extension.Complete;
        var blockComplete = reviewed && CandidateCompletesBlock(state);
        foreach (var requirement in state.Construction!.Requirements.Where(r => complete || step.RequirementIds.Contains(r.Id, StringComparer.Ordinal)))
            requirement.Status = requirement.Status == "closed" || complete || blockComplete && step.CloseRequirementIds.Contains(requirement.Id, StringComparer.Ordinal)
                ? "closed" : "in_progress";
        if (continuation is null)
        {
            if (blockComplete) AcceptContinuation(state);
            else AcceptImplementedConsumer(state);
            compact.CurrentStep = null;
        }
        else ContinueAcceptedBlock(state, continuation);
        if (blockComplete && CurrentBlock(state) is { } block)
        {
            block.CompletionCheckpoint = state.Construction.Checkpoints.Count;
            compact.CurrentBlockId = null;
        }
        compact.PendingCommitments.Clear();
        compact.ReplacementScope = null; compact.ReplacementRequirements = null;
        compact.RequirementPageRequested = false; compact.RequirementOffset = 0;
    }

    internal static bool SourcesComplete(PlanningSession state)
    {
        var compact = state.Construction?.Compact;
        if (compact is null || compact.Sources.Count == 0 || compact.CurrentBlockId is not null || compact.CurrentStep is not null || compact.PendingCommitments.Count > 0 || !IntentDeclarationsComplete(compact) ||
            compact.IndexedAnswers != (state.AnswerHistory?.Count ?? 0) || compact.Sources.Any(s => !s.Indexed || s.Version != PlanningGraphCompiler.Fingerprint(s.Text)) ||
            string.Concat(compact.Sources.Where(s => s.Kind is "page" or "revision_page").Select(s => s.Text)) != state.Request.Prompt) return false;
        foreach (var page in compact.Sources.Where(s => s.Kind is "page" or "answer_page" or "revision_page"))
        {
            if (string.Concat(compact.Sources.Where(s => s.Id.StartsWith(page.Id + ":clause:", StringComparison.Ordinal)).Select(s => s.Text)) != page.Text) return false;
            if (page.ContinuationOf is not null && compact.Sources.First(s => s.Id.StartsWith(page.Id + ":clause:", StringComparison.Ordinal)).ContinuationOf !=
                compact.Sources.LastOrDefault(s => s.Id.StartsWith(page.ContinuationOf + ":clause:", StringComparison.Ordinal))?.Id) return false;
        }
        foreach (var continuation in compact.Sources.Where(s => s.Kind is "requirement" or "global" && s.ContinuationOf is not null))
        {
            var previous = compact.Sources.SingleOrDefault(s => s.Id == continuation.ContinuationOf);
            if (previous is null || previous.Kind != continuation.Kind || continuation.Kind == "requirement" &&
                !compact.RequirementSources.Values.Any(ids => ids.Contains(previous.Id, StringComparer.Ordinal) &&
                    ids.Contains(continuation.Id, StringComparer.Ordinal))) return false;
        }
        foreach (var requirement in state.Requirements!.Outcomes)
            if (!compact.RequirementSources.TryGetValue(requirement.Id, out var sources) || sources.Count == 0 ||
                sources.Any(id => compact.Sources.Count(s => s.Id == id && s.Kind == "requirement") != 1) ||
                string.Concat(sources.Select(id => compact.Sources.Single(s => s.Id == id).Text)) != requirement.Description) return false;
        return compact.Sources.Where(s => s.Kind == "requirement").All(s => compact.RequirementSources.Values.Any(ids => ids.Contains(s.Id, StringComparer.Ordinal))) &&
            compact.Sources.Where(s => s.Kind == "global").All(s => compact.GlobalSourceIds.Contains(s.Id, StringComparer.Ordinal));
    }

    internal static bool LoadEvidence(PlanningSession state, string address, out JsonNode? value, JsonNode? issuedSchema = null)
    {
        var compact = State(state); value = null;
        if (LoadProviderRequestEvidence(state, address, out value)) return true;
        if (LoadScopeInterfaceEvidence(state, address, out value)) return true;
        if (LoadTaskNavigationEvidence(state, address, out value)) return true;
        if (address.StartsWith("source-description:", StringComparison.Ordinal))
        { value = LoadContractEvidence(state, address); return true; }
        if (address.StartsWith("schema:", StringComparison.Ordinal) || address.StartsWith("schema-page:", StringComparison.Ordinal) || address.StartsWith("port-page:", StringComparison.Ordinal) ||
            address.StartsWith("description:", StringComparison.Ordinal) || address.StartsWith("contract:", StringComparison.Ordinal))
        {
            if (compact.CurrentStep is null && (!address.StartsWith("schema:", StringComparison.Ordinal) && !address.StartsWith("schema-page:", StringComparison.Ordinal) &&
                !address.StartsWith("port-page:", StringComparison.Ordinal) && !address.StartsWith("description:", StringComparison.Ordinal)))
                PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Select a business step before inspecting its capability contracts.");
            value = LoadContractEvidence(state, address); return true;
        }
        if (address.StartsWith("source:", StringComparison.Ordinal))
        {
            var source = compact.Sources.SingleOrDefault(s => s.Id == address[7..]);
            if (source is not null) value = new JsonObject { ["id"] = source.Id, ["version"] = source.Version, ["text"] = source.Text };
            return true;
        }
        if (address.StartsWith("input:", StringComparison.Ordinal))
        {
            var input = state.Requirements?.Inputs?.SingleOrDefault(i => i.Name == address[6..]);
            if (input is not null) value = JsonSerializer.SerializeToNode(input, PlanningJsonContext.Default.TaskInput);
            return true;
        }
        if (address.StartsWith("output:", StringComparison.Ordinal))
        {
            var output = state.Requirements?.Outputs?.SingleOrDefault(o => o.Name == address[7..]);
            if (output is not null) value = JsonSerializer.SerializeToNode(output, PlanningJsonContext.Default.TaskInput);
            return true;
        }
        if (address.StartsWith("choice:", StringComparison.Ordinal))
        {
            var choice = (state.Construction!.Candidate?.Plan ?? state.Plan)?.Choices.SingleOrDefault(c => c.Id == address[7..]);
            if (choice is not null) value = JsonSerializer.SerializeToNode(choice, PlanningJsonContext.Default.PlanningChoice);
            return true;
        }
        if (address.StartsWith("group:", StringComparison.Ordinal))
        { value = GroupInterface(state, address[6..]); return true; }
        if (address.StartsWith("requirement:", StringComparison.Ordinal))
        { value = RequirementNodes(state, [address[12..]]); if (value.AsArray().Count == 0) value = null; return true; }
        var parts = address.Split(':');
        if (parts is ["rejected", var rejected] && int.TryParse(rejected, out var rejectedIndex) && rejectedIndex >= 0 && rejectedIndex < compact.RejectedSteps.Count)
        { value = compact.RejectedSteps[rejectedIndex].DeepClone(); return true; }
        if (parts is ["page", "sources", var sourceOffset] && int.TryParse(sourceOffset, out var sourceIndex) && sourceIndex >= 0)
        { value = SourceDescriptors(state, sourceIndex); return true; }
        if (parts is ["page", "requirements", var requirementOffset] && int.TryParse(requirementOffset, out var next) && next >= 0 && next < state.Requirements!.Outcomes.Count)
        {
            var requirements = RequirementPage(state, next);
            var following = next + requirements.Length;
            value = new JsonObject { ["offset"] = next, ["total"] = state.Requirements.Outcomes.Count,
                ["items"] = RequirementNodes(state, requirements.Select(r => r.Id)),
                ["next"] = following < state.Requirements.Outcomes.Count ? "page:requirements:" + following : null };
            {
                compact.RequirementOffset = Math.Max(compact.RequirementOffset, following - 1);
                compact.RequirementPageRequested = true;
            }
            return true;
        }
        if (parts is ["page", "interfaces", var interfaceOffset] && int.TryParse(interfaceOffset, out next) && next >= 0)
        { compact.InterfaceOffset = next; value = InterfaceDirectory(state, null, next); return true; }
        if (parts is ["page", "outputs", var outputOffset] && int.TryParse(outputOffset, out next) && next >= 0 &&
            (next == 0 || next < (state.Requirements?.Outputs?.Count ?? 0)))
        { compact.OutputOffset = next; value = OutputDirectory(state); return true; }
        if (LoadOperationPage(state, address, out value, issuedSchema)) return true;
        if (parts is ["page", "checkpoints", var checkpointOffset] && int.TryParse(checkpointOffset, out next) && next >= 0 && next < state.Construction!.Checkpoints.Count)
        {
            value = new JsonObject { ["offset"] = next, ["total"] = state.Construction.Checkpoints.Count,
                ["items"] = new JsonArray(state.Construction.Checkpoints.Skip(next).Take(4).Select(c => (JsonNode)new JsonObject
                { ["sequence"] = c.Sequence, ["hash"] = c.PlanHash, ["fragment"] = c.Fragment.DeepClone(),
                    ["step"] = JsonSerializer.SerializeToNode(c.Step, PlanningJsonContext.Default.PlanningConstructionStep) }).ToArray()) };
            value["next"] = next + 4 < state.Construction.Checkpoints.Count ? "page:checkpoints:" + (next + 4) : null;
            return true;
        }
        return false;
    }
}
