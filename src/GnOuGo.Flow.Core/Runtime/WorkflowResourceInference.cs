using System.Globalization;
using System.Text;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal static class WorkflowResourceInference
{
    public const string ExistingWorkspaceRelativePathResource = "existing_workspace_relative_path";
    public const string ExistingInputResourceRole = "existing-input";
    public const string CreationTargetInputResourceRole = "creation-target-input";
    public const string ProducedOutputResourceRole = "produced-output";

    public static string? InferRequiredResourceKind(string fieldName, InputDef inputDef)
    {
        if (!WorkflowContractAllowsString(inputDef.Type))
            return null;

        return LooksLikeRequiredExistingWorkspaceRelativePath(fieldName, inputDef.Description)
            ? ExistingWorkspaceRelativePathResource
            : null;
    }

    public static string? InferProducedResourceKind(string fieldName, OutputDef outputDef)
    {
        if (!WorkflowContractAllowsString(outputDef.Type))
            return null;

        return LooksLikeProducedExistingWorkspaceRelativePath(
                fieldName,
                outputDef.Description,
                allowNameOnlyForMcpOutput: false)
            ? ExistingWorkspaceRelativePathResource
            : null;
    }

    public static bool LooksLikeExistingWorkspaceRelativePathKind(string text)
    {
        var normalized = NormalizeResourceText(text);
        return normalized.Contains("existing_workspace_relative_path", StringComparison.Ordinal)
               || normalized.Contains("existing workspace relative path", StringComparison.Ordinal)
               || normalized.Contains("existing-workspace-relative-path", StringComparison.Ordinal);
    }

    public static bool LooksLikeRequiredExistingWorkspaceRelativePath(string fieldName, string? description)
    {
        var text = NormalizeResourceText(fieldName + " " + description);
        if (HasCreationTargetInputSignal(text))
            return false;

        return HasPathSignal(text)
               && HasRelativeWorkspaceSignal(text)
               && HasExistingResourceSignal(text);
    }

    public static bool LooksLikeProducedExistingWorkspaceRelativePath(
        string fieldName,
        string? description,
        bool allowNameOnlyForMcpOutput)
    {
        var text = NormalizeResourceText(fieldName + " " + description);
        if (!HasPathSignal(text) || !HasRelativeWorkspaceSignal(text))
            return false;

        return HasExistingResourceSignal(text)
               || HasProducerSignal(text)
               || allowNameOnlyForMcpOutput && FieldNameLooksLikeRelativePathOutput(fieldName);
    }

    public static bool LooksPathLikeButUnproven(string fieldName, string? description)
    {
        var text = NormalizeResourceText(fieldName + " " + description);
        return HasPathSignal(text) && HasRelativeWorkspaceSignal(text);
    }

    public static bool RoleAllowsRequiredExistingResource(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return true;

        var normalized = NormalizeResourceRole(role);
        return normalized is "existinginput"
            or "input"
            or "existing"
            or "requiredinput"
            or "required"
            or "requiredexisting"
            or "consumer"
            or "consumesexisting"
            or "existingresourceconsumer";
    }

    public static bool RoleAllowsProducedExistingResource(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return true;

        var normalized = NormalizeResourceRole(role);
        return normalized is "producedoutput"
            or "producer"
            or "produces"
            or "output"
            or "produced"
            or "created"
            or "createdoutput"
            or "existingresourceproducer";
    }

    public static bool RoleIsCreationTargetInput(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return false;

        var normalized = NormalizeResourceRole(role);
        return normalized is "creationtargetinput"
            or "creationtarget"
            or "createtarget"
            or "targettocreate"
            or "destination"
            or "destinationinput";
    }

    private static bool HasPathSignal(string text) =>
        text.Contains("path", StringComparison.Ordinal)
        || text.Contains("root", StringComparison.Ordinal)
        || text.Contains("directory", StringComparison.Ordinal)
        || text.Contains("dir", StringComparison.Ordinal)
        || text.Contains("folder", StringComparison.Ordinal)
        || text.Contains("workspace", StringComparison.Ordinal)
        || text.Contains("repository", StringComparison.Ordinal)
        || text.Contains("project", StringComparison.Ordinal)
        || text.Contains("chemin", StringComparison.Ordinal)
        || text.Contains("dossier", StringComparison.Ordinal)
        || text.Contains("depot", StringComparison.Ordinal)
        || text.Contains("racine", StringComparison.Ordinal);

    private static bool HasRelativeWorkspaceSignal(string text) =>
        text.Contains("workspace-relative", StringComparison.Ordinal)
        || text.Contains("workspace relative", StringComparison.Ordinal)
        || text.Contains("relative to the workspace", StringComparison.Ordinal)
        || text.Contains("relative path", StringComparison.Ordinal)
        || text.Contains("path relative", StringComparison.Ordinal)
        || text.Contains("relative", StringComparison.Ordinal)
        || text.Contains("relatif", StringComparison.Ordinal);

    private static bool HasExistingResourceSignal(string text) =>
        ContainsPositiveExistingToken(text)
        || text.Contains("must exist", StringComparison.Ordinal)
        || text.Contains("already exists", StringComparison.Ordinal)
        || text.Contains("pre-existing", StringComparison.Ordinal)
        || text.Contains("previous tool", StringComparison.Ordinal)
        || text.Contains("previous step", StringComparison.Ordinal)
        || text.Contains("created by", StringComparison.Ordinal)
        || text.Contains("produced by", StringComparison.Ordinal)
        || text.Contains("existant", StringComparison.Ordinal)
        || text.Contains("existe", StringComparison.Ordinal)
        || text.Contains("precedent", StringComparison.Ordinal);

    private static bool HasCreationTargetInputSignal(string text) =>
        text.Contains("creation target", StringComparison.Ordinal)
        || text.Contains("create target", StringComparison.Ordinal)
        || text.Contains("target to create", StringComparison.Ordinal)
        || text.Contains("non-existing", StringComparison.Ordinal)
        || text.Contains("non existing", StringComparison.Ordinal)
        || text.Contains("does not exist", StringComparison.Ordinal)
        || text.Contains("must not exist", StringComparison.Ordinal)
        || text.Contains("empty or non", StringComparison.Ordinal)
        || text.Contains("new workspace", StringComparison.Ordinal)
        || text.Contains("new repository", StringComparison.Ordinal)
        || text.Contains("new project", StringComparison.Ordinal)
        || text.Contains("new directory", StringComparison.Ordinal)
        || text.Contains("new folder", StringComparison.Ordinal)
        || text.Contains("clone target", StringComparison.Ordinal)
        || text.Contains("cloned into", StringComparison.Ordinal)
        || text.Contains("output directory", StringComparison.Ordinal)
        || text.Contains("output folder", StringComparison.Ordinal)
        || text.Contains("destination directory", StringComparison.Ordinal)
        || text.Contains("destination folder", StringComparison.Ordinal)
        || text.Contains("dossier a creer", StringComparison.Ordinal)
        || text.Contains("repertoire a creer", StringComparison.Ordinal);

    private static bool ContainsPositiveExistingToken(string text)
    {
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = token.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '`', '(', ')', '[', ']', '{', '}');
            if (normalized is "existing" or "exists")
                return true;
        }

        return false;
    }

    private static bool HasProducerSignal(string text) =>
        text.Contains("created", StringComparison.Ordinal)
        || text.Contains("creates", StringComparison.Ordinal)
        || text.Contains("produced", StringComparison.Ordinal)
        || text.Contains("produces", StringComparison.Ordinal)
        || text.Contains("materialized", StringComparison.Ordinal)
        || text.Contains("cloned", StringComparison.Ordinal)
        || text.Contains("cree", StringComparison.Ordinal);

    private static bool FieldNameLooksLikeRelativePathOutput(string fieldName)
    {
        var text = NormalizeResourceText(fieldName);
        return HasPathSignal(text)
               && (text.Contains("relative", StringComparison.Ordinal)
                   || text.EndsWith("rel", StringComparison.Ordinal));
    }

    private static bool WorkflowContractAllowsString(string? typeName) =>
        string.Equals(typeName, "string", StringComparison.OrdinalIgnoreCase)
        || string.Equals(typeName, "any", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeResourceRole(string role)
    {
        var text = NormalizeResourceText(role);
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    private static string NormalizeResourceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var chars = decomposed
            .Where(static c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c)
            .ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC);
    }
}
