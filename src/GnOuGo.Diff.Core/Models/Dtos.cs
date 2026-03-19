namespace GnOuGo.Diff.Core.Models;

/// <summary>
/// DTO pour créer une nouvelle révision
/// </summary>
public record CreateRevisionRequest(
    string EntityType,
    string EntityId,
    string CurrentValue,
    string Author
);

/// <summary>
/// DTO pour une révision avec informations de diff
/// </summary>
public record RevisionDto(
    Guid Id,
    string EntityType,
    string EntityId,
    DateTimeOffset Timestamp,
    string Author,
    string CurrentValue,
    string? DiffFromPrevious,
    bool IsFirstRevision
);

/// <summary>
/// DTO pour comparer deux révisions
/// </summary>
public record ComparisonResult(
    RevisionDto FromRevision,
    RevisionDto ToRevision,
    string UnifiedDiff,
    DiffStats Stats
);

/// <summary>
/// Statistiques d'un diff
/// </summary>
public record DiffStats(
    int LinesAdded,
    int LinesDeleted,
    int LinesModified,
    int LinesUnchanged
);

