namespace GnOuGo.Code.Mcp;

public interface ICodeAssistantClient
{
    Task<CodeSuggestionResult> SuggestChangeAsync(
        string task,
        string projectRoot,
        IReadOnlyList<CodeFileContent> contextFiles,
        CancellationToken cancellationToken);
}


