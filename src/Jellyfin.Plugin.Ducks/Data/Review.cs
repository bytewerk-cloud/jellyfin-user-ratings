using System;

namespace Jellyfin.Plugin.Ducks.Data;

/// <summary>
/// One user's rating (and optional review) of one item.
/// Ratings are stored as half-duck units (0-8) to avoid floating point drift.
/// </summary>
public sealed class Review
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the rating in half ducks: 0 = 0 ducks, 8 = 4 ducks.</summary>
    public int HalfDucks { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets a snapshot of the item, used to re-link the row if Jellyfin assigns a new item id.</summary>
    public ItemSnapshot Item { get; set; } = new();

    public double Ducks => HalfDucks / 2.0;

    public bool IsEdited => UpdatedUtc > CreatedUtc;
}

/// <summary>
/// Item details captured at rating time. Survives library rebuilds that change item ids.
/// </summary>
public sealed class ItemSnapshot
{
    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Path { get; set; }

    /// <summary>Gets or sets provider ids serialized as JSON, e.g. {"Imdb":"tt0111161"}.</summary>
    public string? ProviderIdsJson { get; set; }

    public string? SeriesName { get; set; }

    public int? ProductionYear { get; set; }
}

/// <summary>
/// Average rating and count for one item.
/// </summary>
public sealed record Aggregate(Guid ItemId, double Average, int Count);
