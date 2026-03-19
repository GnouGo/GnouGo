namespace GnOuGo.Diff.Core.Models;

/// <summary>
/// Représente une révision d'une entité avec son diff par rapport à la version précédente
/// </summary>
public class DiffEntry
{
    /// <summary>
    /// ID unique de l'entrée de diff (GUID timestampé)
    /// </summary>
    public Guid Id { get; set; }
    
    /// <summary>
    /// Type de l'entité (ex: "User", "Configuration", "Order")
    /// </summary>
    public required string EntityType { get; set; }
    
    /// <summary>
    /// ID unique de l'entité dans son système source
    /// </summary>
    public required string EntityId { get; set; }
    
    /// <summary>
    /// Ticks UTC du timestamp (stocké en base de données)
    /// </summary>
    public long TimestampTicks { get; set; }
    
    /// <summary>
    /// Date et heure de création de cette révision (UTC) - propriété calculée
    /// </summary>
    public DateTimeOffset Timestamp
    {
        get => new DateTimeOffset(TimestampTicks, TimeSpan.Zero);
        set => TimestampTicks = value.UtcTicks;
    }
    
    /// <summary>
    /// Auteur de la modification
    /// </summary>
    public required string Author { get; set; }
    
    /// <summary>
    /// Valeur complète de l'entité à cette révision (JSON sérialisé)
    /// </summary>
    public required string CurrentValue { get; set; }
    
    /// <summary>
    /// Diff par rapport à la version précédente (format DiffPlex)
    /// Null pour la première version
    /// </summary>
    public string? DiffFromPrevious { get; set; }
    
    /// <summary>
    /// Hash de la valeur courante pour détecter les doublons
    /// </summary>
    public required string ValueHash { get; set; }
}

