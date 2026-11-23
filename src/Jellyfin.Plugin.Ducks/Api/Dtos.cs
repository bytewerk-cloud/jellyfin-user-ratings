using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ducks.Api;

/// <summary>Body for PUT /Ducks/Items/{itemId}.</summary>
public sealed class SaveRatingRequest
{
    /// <summary>Gets or sets the rating in ducks, 0 to 4 in steps of 0.5.</summary>
    public double Rating { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }
}

public sealed class MyReviewDto
{
    public double Rating { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public bool IsEdited { get; set; }
}

public sealed class AggregateDto
{
    public double Average { get; set; }

    public int Count { get; set; }
}

public sealed class ItemRatingDto
{
    public Guid ItemId { get; set; }

    public MyReviewDto? Mine { get; set; }

    public AggregateDto? Aggregate { get; set; }

    public int MaxTitleLength { get; set; }

    public int MaxBodyLength { get; set; }
}

public sealed class AggregatesRequest
{
    public List<Guid> ItemIds { get; set; } = [];
}

public sealed class FeedEntryDto
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public bool UserHasImage { get; set; }

    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public string? ItemType { get; set; }

    public string? SeriesName { get; set; }

    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the item whose Primary image best represents this entry (episode → series, etc.).</summary>
    public Guid? ImageItemId { get; set; }

    public string? ImageTag { get; set; }

    public double Rating { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public bool IsEdited { get; set; }
}

public sealed class FeedPageDto
{
    public List<FeedEntryDto> Items { get; set; } = [];

    /// <summary>Gets or sets the opaque cursor for the next page; null when there are no more entries.</summary>
    public string? Next { get; set; }
}

public sealed class StatusDto
{
    public string? Version { get; set; }

    /// <summary>Gets or sets a value indicating whether the web UI script is being injected (File Transformation present).</summary>
    public bool WebInjectionActive { get; set; }

    public string DatabasePath { get; set; } = string.Empty;

    public int RatingCount { get; set; }

    public string? LatestBackup { get; set; }
}
