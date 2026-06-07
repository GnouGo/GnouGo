using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Default workflow.call resolver. Supports the built-in <c>local</c>, <c>url</c>,
/// and <c>workspace</c> reference kinds.
/// </summary>
public class DefaultWorkflowCallResolver : IWorkflowCallResolver
{
    private readonly string? _workspaceRoot;
    private readonly HashSet<string> _allowedHostnames;

    public DefaultWorkflowCallResolver(
        string? workspaceRoot = null,
        IEnumerable<string>? allowedHostnames = null)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? null
            : Path.GetFullPath(workspaceRoot);
        _allowedHostnames = new HashSet<string>(
            allowedHostnames?.Where(static h => !string.IsNullOrWhiteSpace(h)).Select(static h => h.Trim())
                ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public virtual async Task<WorkflowCallResolution> ResolveAsync(WorkflowCallResolutionContext context, CancellationToken ct)
    {
        return context.Kind switch
        {
            "local" => ResolveLocal(context),
            "url" => await ResolveUrlAsync(context, ct),
            "workspace" => await ResolveWorkspaceAsync(context, ct),
            _ => throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Unknown workflow.call kind: {context.Kind}")
        };
    }

    protected static string? GetString(JsonObject obj, string propertyName)
        => obj[propertyName]?.GetValue<string>();

    protected static WorkflowCallResolution CompileDocumentReference(string yaml, JsonObject refObj, string sourceDescription)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        var workflow = SelectWorkflow(document, compiled, refObj, sourceDescription);

        return new WorkflowCallResolution
        {
            Workflow = workflow,
            WorkflowName = workflow.Name,
            CallStackKey = $"{sourceDescription}:{workflow.Name}"
        };
    }

    private static WorkflowCallResolution ResolveLocal(WorkflowCallResolutionContext context)
    {
        var name = GetString(context.Ref, "name")
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Local workflow.call requires 'name'");

        var callStackKey = $"local:{name}";
        if (context.CallStack.Contains(callStackKey))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowCycleDetected,
                $"Cycle detected: workflow '{name}' already in call stack");

        var compiledDoc = context.Engine.CompiledDocument;
        if (compiledDoc == null || !compiledDoc.Workflows.TryGetValue(name, out var workflow))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, $"Local workflow '{name}' not found");

        return new WorkflowCallResolution
        {
            Workflow = workflow,
            WorkflowName = name,
            CallStackKey = callStackKey
        };
    }

    private async Task<WorkflowCallResolution> ResolveUrlAsync(WorkflowCallResolutionContext context, CancellationToken ct)
    {
        var url = GetString(context.Ref, "url")
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Remote workflow.call requires 'url'");

        var fetcher = context.Engine.WorkflowFetcher
            ?? throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchNetwork, "No workflow fetcher configured");

        var integrity = GetString(context.Ref, "integrity");
        EnforceFetchPolicy(url, integrity, context.Engine.FetchPolicy);

        var yaml = await fetcher.FetchAsync(url, integrity, ct);
        EnforceMaxSize(yaml, context.Engine.FetchPolicy);

        return CompileDocumentReference(yaml, context.Ref, url);
    }

    private async Task<WorkflowCallResolution> ResolveWorkspaceAsync(WorkflowCallResolutionContext context, CancellationToken ct)
    {
        if (_workspaceRoot is null)
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "No workspace root configured for workflow.call kind 'workspace'");

        var relativePath = GetString(context.Ref, "path") ?? GetString(context.Ref, "name")
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Workspace workflow.call requires 'path'");

        var fullPath = ResolveWorkspacePath(relativePath);
        if (!File.Exists(fullPath))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchNetwork, $"Workspace workflow '{relativePath}' not found");

        var yaml = await File.ReadAllTextAsync(fullPath, ct);
        EnforceMaxSize(yaml, context.Engine.FetchPolicy);

        return CompileDocumentReference(yaml, context.Ref, $"workspace:{relativePath.Replace('\\', '/')}");
    }

    private void EnforceFetchPolicy(string url, string? integrity, FetchPolicy? policy)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Remote workflow.call requires an absolute URL");

        if ((policy?.RequireHttps ?? false) && uri.Scheme != Uri.UriSchemeHttps)
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "HTTPS required by policy");

        IEnumerable<string> allowedHostnames = policy?.AllowedHostnames.Count > 0
            ? policy.AllowedHostnames
            : _allowedHostnames;
        var allowedHostnamesList = allowedHostnames as IReadOnlyCollection<string> ?? allowedHostnames.ToArray();
        if (allowedHostnamesList.Count > 0 && !allowedHostnamesList.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, $"Host '{uri.Host}' not in allow-list");

        if ((policy?.RequireIntegrity ?? false) && string.IsNullOrWhiteSpace(integrity))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "Integrity hash required by fetch policy but missing");
    }

    private static void EnforceMaxSize(string yaml, FetchPolicy? policy)
    {
        if (policy is null || policy.MaxSizeBytes <= 0)
            return;

        var size = System.Text.Encoding.UTF8.GetByteCount(yaml);
        if (size > policy.MaxSizeBytes)
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                $"Remote workflow ({size} bytes) exceeds max_size_bytes ({policy.MaxSizeBytes})");
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "Workspace workflow path must be relative");

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot!, normalized));
        if (!IsPathWithinRoot(fullPath, _workspaceRoot!))
            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy, "Workspace workflow path traversal is not allowed");

        return fullPath;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledWorkflow SelectWorkflow(
        WorkflowDocument document,
        CompiledDocument compiled,
        JsonObject refObj,
        string sourceDescription)
    {
        var exportName = GetString(refObj, "export");
        if (!string.IsNullOrWhiteSpace(exportName))
        {
            if (document.Exports is { Count: > 0 } && !document.Exports.Contains(exportName, StringComparer.Ordinal))
                throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                    $"Workflow '{exportName}' is not exported from {sourceDescription}");

            if (compiled.Workflows.TryGetValue(exportName, out var exportedWorkflow))
                return exportedWorkflow;

            throw new WorkflowRuntimeException(ErrorCodes.WorkflowFetchPolicy,
                $"Workflow '{exportName}' is not defined in {sourceDescription}");
        }

        if (document.Exports is { Count: 1 } && compiled.Workflows.TryGetValue(document.Exports[0], out var onlyExport))
            return onlyExport;

        if (!string.IsNullOrWhiteSpace(compiled.Entrypoint) && compiled.Workflows.TryGetValue(compiled.Entrypoint, out var entrypoint))
            return entrypoint;

        if (compiled.Workflows.Count == 1)
            return compiled.Workflows.Values.First();

        throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
            $"Could not resolve target workflow from {sourceDescription}");
    }
}


