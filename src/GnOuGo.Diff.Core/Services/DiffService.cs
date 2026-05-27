using System.Security.Cryptography;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.EntityFrameworkCore;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Models;

namespace GnOuGo.Diff.Core.Services;

public class DiffService
{
    private readonly DiffDbContext _context;
    private readonly ISideBySideDiffBuilder _diffBuilder;

    public DiffService(DiffDbContext context)
    {
        _context = context;
        _diffBuilder = new SideBySideDiffBuilder(new Differ());
    }

    /// <summary>
    /// Crée une nouvelle révision pour une entité
    /// </summary>
    public async Task<RevisionDto> CreateRevisionAsync(CreateRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var valueHash = ComputeHash(request.CurrentValue);
        
        // Récupérer la dernière révision pour cette entité
        var lastRevision = await _context.DiffEntries
            .Where(e => e.EntityType == request.EntityType && e.EntityId == request.EntityId)
            .OrderByDescending(e => e.TimestampTicks)
            .FirstOrDefaultAsync(ct);

        // Vérifier si la valeur a changé
        if (!request.ForceCreate && lastRevision != null && lastRevision.ValueHash == valueHash)
        {
            // Aucun changement, retourner la dernière révision
            return MapToDto(lastRevision);
        }

        // Calculer le diff si ce n'est pas la première révision
        string? diffFromPrevious = null;
        if (lastRevision != null)
        {
            var diffResult = _diffBuilder.BuildDiffModel(lastRevision.CurrentValue, request.CurrentValue);
            diffFromPrevious = SerializeDiff(diffResult);
        }

        var entry = new DiffEntry
        {
            Id = Guid.CreateVersion7(),
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            Timestamp = DateTimeOffset.UtcNow,
            Author = request.Author,
            CurrentValue = request.CurrentValue,
            DiffFromPrevious = diffFromPrevious,
            ValueHash = valueHash
        };

        _context.DiffEntries.Add(entry);
        await _context.SaveChangesAsync(ct);

        return MapToDto(entry);
    }

    /// <summary>
    /// Récupère toutes les révisions d'une entité
    /// </summary>
    public async Task<List<RevisionDto>> GetRevisionsAsync(string entityType, string entityId)
    {
        var entries = await _context.DiffEntries
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.TimestampTicks)
            .ToListAsync();

        return entries.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Récupère une révision à un timestamp spécifique (ou la plus proche avant)
    /// </summary>
    public async Task<RevisionDto?> GetRevisionAtTimestampAsync(string entityType, string entityId, DateTimeOffset timestamp)
    {
        var timestampTicks = timestamp.UtcTicks;
        var entry = await _context.DiffEntries
            .Where(e => e.EntityType == entityType && e.EntityId == entityId && e.TimestampTicks <= timestampTicks)
            .OrderByDescending(e => e.TimestampTicks)
            .FirstOrDefaultAsync();

        return entry != null ? MapToDto(entry) : null;
    }

    /// <summary>
    /// Compare deux révisions et génère un diff unifié
    /// </summary>
    public async Task<ComparisonResult?> CompareRevisionsAsync(Guid fromRevisionId, Guid toRevisionId)
    {
        var fromRevision = await _context.DiffEntries.FindAsync(fromRevisionId);
        var toRevision = await _context.DiffEntries.FindAsync(toRevisionId);

        if (fromRevision == null || toRevision == null)
            return null;

        if (fromRevision.EntityType != toRevision.EntityType || fromRevision.EntityId != toRevision.EntityId)
            throw new InvalidOperationException("Cannot compare revisions from different entities");

        var diffResult = _diffBuilder.BuildDiffModel(fromRevision.CurrentValue, toRevision.CurrentValue);
        var unifiedDiff = GenerateUnifiedDiff(diffResult);
        var stats = CalculateStats(diffResult);

        return new ComparisonResult(
            MapToDto(fromRevision),
            MapToDto(toRevision),
            unifiedDiff,
            stats
        );
    }

    /// <summary>
    /// Liste tous les types d'entités avec le nombre de révisions
    /// </summary>
    public async Task<Dictionary<string, int>> GetEntityTypesAsync()
    {
        return await _context.DiffEntries
            .GroupBy(e => e.EntityType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Type, x => x.Count);
    }

    /// <summary>
    /// Liste toutes les entités d'un type avec leur dernière révision
    /// </summary>
    public async Task<List<RevisionDto>> GetLatestRevisionsForTypeAsync(string entityType)
    {
        // Récupérer toutes les entrées, puis trier côté client
        var allEntries = await _context.DiffEntries
            .Where(e => e.EntityType == entityType)
            .ToListAsync();

        var latestRevisions = allEntries
            .GroupBy(e => e.EntityId)
            .Select(g => g.OrderByDescending(e => e.TimestampTicks).First())
            .ToList();

        return latestRevisions.Select(MapToDto).ToList();
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string SerializeDiff(SideBySideDiffModel diff)
    {
        // Sérialiser simplement les lignes modifiées
        var lines = new List<string>();
        
        foreach (var line in diff.OldText.Lines)
        {
            if (line.Type == ChangeType.Deleted || line.Type == ChangeType.Modified)
            {
                lines.Add($"- {line.Text}");
            }
        }
        
        foreach (var line in diff.NewText.Lines)
        {
            if (line.Type == ChangeType.Inserted || line.Type == ChangeType.Modified)
            {
                lines.Add($"+ {line.Text}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string GenerateUnifiedDiff(SideBySideDiffModel diff)
    {
        var lines = new List<string>();
        
        for (int i = 0; i < Math.Max(diff.OldText.Lines.Count, diff.NewText.Lines.Count); i++)
        {
            if (i < diff.OldText.Lines.Count)
            {
                var oldLine = diff.OldText.Lines[i];
                if (oldLine.Type == ChangeType.Deleted)
                {
                    lines.Add($"- {oldLine.Text}");
                }
                else if (oldLine.Type == ChangeType.Modified)
                {
                    lines.Add($"- {oldLine.Text}");
                }
                else if (oldLine.Type == ChangeType.Unchanged)
                {
                    lines.Add($"  {oldLine.Text}");
                }
            }
            
            if (i < diff.NewText.Lines.Count)
            {
                var newLine = diff.NewText.Lines[i];
                if (newLine.Type == ChangeType.Inserted)
                {
                    lines.Add($"+ {newLine.Text}");
                }
                else if (newLine.Type == ChangeType.Modified)
                {
                    lines.Add($"+ {newLine.Text}");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static DiffStats CalculateStats(SideBySideDiffModel diff)
    {
        int added = diff.NewText.Lines.Count(l => l.Type == ChangeType.Inserted);
        int deleted = diff.OldText.Lines.Count(l => l.Type == ChangeType.Deleted);
        int modified = diff.NewText.Lines.Count(l => l.Type == ChangeType.Modified);
        int unchanged = diff.NewText.Lines.Count(l => l.Type == ChangeType.Unchanged);

        return new DiffStats(added, deleted, modified, unchanged);
    }

    private static RevisionDto MapToDto(DiffEntry entry)
    {
        return new RevisionDto(
            entry.Id,
            entry.EntityType,
            entry.EntityId,
            entry.Timestamp,
            entry.Author,
            entry.CurrentValue,
            entry.DiffFromPrevious,
            entry.DiffFromPrevious == null
        );
    }
}

