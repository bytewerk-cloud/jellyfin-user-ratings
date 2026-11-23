using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Ducks.Api;
using Jellyfin.Plugin.Ducks.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ducks.Services;

/// <summary>
/// Business rules on top of <see cref="DucksRepository"/>: access checks, validation, Jellyfin user data mirroring.
/// </summary>
public sealed class DucksService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<DucksService> _logger;
    private readonly Lazy<DucksRepository> _repository;

    public DucksService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IApplicationPaths applicationPaths,
        ILogger<DucksService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        DataDirectory = Path.Combine(applicationPaths.DataPath, "ducks");
        _repository = new Lazy<DucksRepository>(() =>
        {
            var repo = new DucksRepository(Path.Combine(DataDirectory, "ducks.db"));
            _logger.LogInformation("Ducks database ready at {Path}", repo.DatabasePath);
            return repo;
        });
    }

    /// <summary>Gets the folder holding the database and its backups. Outside the plugin folder on purpose.</summary>
    public string DataDirectory { get; }

    public DucksRepository Repository => _repository.Value;

    /// <summary>Gets the current settings, or defaults when the plugin isn't loaded (tests, early startup).</summary>
    public static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>
    /// Item types that are containers or metadata, not media, and so can't be rated.
    /// </summary>
    private static readonly HashSet<string> NonRatableTypes = new(StringComparer.Ordinal)
    {
        "AggregateFolder", "BasePluginFolder", "Channel", "CollectionFolder", "Folder", "Genre", "LiveTvChannel",
        "ManualPlaylistsFolder", "MusicGenre", "Person", "Playlist", "PlaylistsFolder", "Studio", "UserRootFolder",
        "UserView", "Year"
    };

    public static bool IsRatable(BaseItem item) => !NonRatableTypes.Contains(item.GetType().Name);

    /// <summary>
    /// Returns the item if it exists, <paramref name="userId"/> may see it, and it is a ratable media item.
    /// </summary>
    public BaseItem? GetVisibleItem(Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        return item is not null && IsRatable(item) ? item : null;
    }

    public ItemRatingDto GetItemRating(Guid userId, Guid itemId)
    {
        var mine = Repository.Get(userId, itemId);
        Repository.GetAggregates([itemId]).TryGetValue(itemId, out var agg);
        return new ItemRatingDto
        {
            ItemId = itemId,
            Mine = mine is null ? null : ToDto(mine),
            Aggregate = agg is null ? null : new AggregateDto { Average = agg.Average, Count = agg.Count },
            MaxTitleLength = Config.MaxTitleLength,
            MaxBodyLength = Config.MaxBodyLength
        };
    }

    /// <summary>
    /// Validates and saves. Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public ItemRatingDto Save(Guid userId, BaseItem item, SaveRatingRequest request)
    {
        int halfDucks;
        try
        {
            halfDucks = DucksRepository.ToHalfDucks(request.Rating);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException("Rating must be 0 to 4 ducks in steps of 0.5.");
        }

        var title = request.Title?.Trim();
        var body = request.Body?.Trim();
        if (title?.Length > Config.MaxTitleLength)
        {
            throw new ArgumentException($"Title is longer than {Config.MaxTitleLength} characters.");
        }

        if (body?.Length > Config.MaxBodyLength)
        {
            throw new ArgumentException($"Review is longer than {Config.MaxBodyLength} characters.");
        }

        Repository.Upsert(userId, item.Id, halfDucks, title, body, Snapshot(item));
        MirrorToUserData(userId, item, halfDucks * 1.25);
        return GetItemRating(userId, item.Id);
    }

    public ItemRatingDto Delete(Guid userId, BaseItem item)
    {
        Repository.Delete(userId, item.Id);
        MirrorToUserData(userId, item, null);
        return GetItemRating(userId, item.Id);
    }

    /// <summary>
    /// Keys use Jellyfin's id format (32 hex chars, no dashes) so the web client can match card data-id attributes.
    /// </summary>
    public Dictionary<string, AggregateDto> GetAggregates(IEnumerable<Guid> itemIds)
    {
        return Repository.GetAggregates(itemIds.Take(1000))
            .ToDictionary(kv => kv.Key.ToString("N", CultureInfo.InvariantCulture), kv => new AggregateDto { Average = kv.Value.Average, Count = kv.Value.Count });
    }

    /// <summary>
    /// Newest-first feed of all users' ratings, limited to items the viewer may see.
    /// </summary>
    public FeedPageDto GetFeed(Guid viewerId, string? cursor, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var viewer = _userManager.GetUserById(viewerId) ?? throw new ArgumentException("Unknown user", nameof(viewerId));
        ParseCursor(cursor, out var beforeCreated, out var beforeId);

        var page = new FeedPageDto();
        var users = new Dictionary<Guid, User?>();
        Review? lastScanned = null;
        var exhausted = false;

        // Some rows may be hidden from this viewer, so keep reading until the page is full.
        while (page.Items.Count < limit)
        {
            var batch = Repository.GetFeedPage(beforeCreated, beforeId, limit * 2);
            foreach (var review in batch)
            {
                lastScanned = review;
                var item = _libraryManager.GetItemById<BaseItem>(review.ItemId, viewer);
                if (item is null)
                {
                    continue;
                }

                if (!users.TryGetValue(review.UserId, out var author))
                {
                    author = _userManager.GetUserById(review.UserId);
                    users[review.UserId] = author;
                }

                page.Items.Add(ToFeedEntry(review, item, author));
                if (page.Items.Count == limit)
                {
                    break;
                }
            }

            var scannedWholeBatch = batch.Count == 0 || ReferenceEquals(lastScanned, batch[^1]);
            if (batch.Count < limit * 2 && scannedWholeBatch)
            {
                exhausted = true;
                break;
            }

            if (lastScanned is not null)
            {
                beforeCreated = lastScanned.CreatedUtc;
                beforeId = lastScanned.Id;
            }
        }

        page.Next = exhausted || lastScanned is null
            ? null
            : DucksRepository.ToUnixMs(lastScanned.CreatedUtc).ToString(CultureInfo.InvariantCulture) + "_" + lastScanned.Id.ToString(CultureInfo.InvariantCulture);
        return page;
    }

    public StatusDto GetStatus(bool webInjectionActive)
    {
        var folder = Path.Combine(DataDirectory, "backups");
        var latest = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "ducks-????????.db").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault()
            : null;
        return new StatusDto
        {
            Version = Plugin.Instance?.Version.ToString(),
            WebInjectionActive = webInjectionActive,
            DatabasePath = Repository.DatabasePath,
            RatingCount = Repository.GetAll().Count,
            LatestBackup = latest is null ? null : Path.GetFileName(latest)
        };
    }

    /// <summary>
    /// Writes backups/ducks-yyyyMMdd.db (one per day, replaced if run again) and keeps the newest <paramref name="keep"/>.
    /// Returns the path written.
    /// </summary>
    public string CreateBackup(DateTime nowUtc, int keep)
    {
        var folder = Path.Combine(DataDirectory, "backups");
        var path = Path.Combine(folder, "ducks-" + nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".db");
        Repository.BackupTo(path);

        // Names sort by date, so ordinal order is chronological.
        var old = Directory.GetFiles(folder, "ducks-????????.db")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(Math.Max(1, keep));
        foreach (var file in old)
        {
            File.Delete(file);
        }

        _logger.LogInformation("Ducks backup written to {Path}", path);
        return path;
    }

    /// <summary>
    /// Finds reviews whose item id no longer exists and moves them to a matching item (same provider ids or path).
    /// Returns the number of rows moved.
    /// </summary>
    public int RelinkOrphans()
    {
        var moved = 0;
        var all = Repository.GetAll();
        foreach (var group in all.GroupBy(r => r.ItemId))
        {
            if (_libraryManager.GetItemById(group.Key) is not null)
            {
                continue;
            }

            var snapshot = group.OrderByDescending(r => r.UpdatedUtc).First().Item;
            var match = FindReplacement(snapshot);
            if (match is null)
            {
                _logger.LogDebug("No replacement yet for orphaned item {ItemId} ({Name})", group.Key, snapshot.Name);
                continue;
            }

            var count = Repository.Relink(group.Key, match.Id);
            _logger.LogInformation("Re-linked {Count} rating(s) for {Name} from {Old} to {New}", count, snapshot.Name, group.Key, match.Id);
            moved += count;
        }

        return moved;
    }

    private BaseItem? FindReplacement(ItemSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(snapshot.Path))
        {
            var byPath = _libraryManager.FindByPath(snapshot.Path, null);
            if (byPath is not null && string.Equals(byPath.GetType().Name, snapshot.Type, StringComparison.Ordinal))
            {
                return byPath;
            }
        }

        if (string.IsNullOrEmpty(snapshot.ProviderIdsJson) || string.IsNullOrEmpty(snapshot.Type)
            || !Enum.TryParse<Jellyfin.Data.Enums.BaseItemKind>(snapshot.Type, out var kind))
        {
            return null;
        }

        var providerIds = JsonSerializer.Deserialize<Dictionary<string, string>>(snapshot.ProviderIdsJson);
        if (providerIds is null || providerIds.Count == 0)
        {
            return null;
        }

        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            HasAnyProviderId = providerIds,
            Recursive = true
        });
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private void MirrorToUserData(Guid userId, BaseItem item, double? rating)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                return;
            }

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            data.Rating = rating;
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The plugin database is the source of truth; the mirror is best effort.
            _logger.LogWarning(ex, "Could not mirror rating to Jellyfin user data for item {ItemId}", item.Id);
        }
    }

    private static ItemSnapshot Snapshot(BaseItem item)
    {
        return new ItemSnapshot
        {
            Name = item.Name,
            Type = item.GetType().Name,
            Path = item.Path,
            ProviderIdsJson = item.ProviderIds is { Count: > 0 } ? JsonSerializer.Serialize(item.ProviderIds) : null,
            SeriesName = (item as Episode)?.SeriesName ?? (item as Season)?.SeriesName,
            ProductionYear = item.ProductionYear
        };
    }

    private static MyReviewDto ToDto(Review r) => new()
    {
        Rating = r.Ducks,
        Title = r.Title,
        Body = r.Body,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
        IsEdited = r.IsEdited
    };

    private static FeedEntryDto ToFeedEntry(Review r, BaseItem item, User? author)
    {
        var imageItem = item;
        if (!item.HasImage(ImageType.Primary, 0))
        {
            imageItem = item switch
            {
                Episode e when e.Series is not null => e.Series,
                Season s when s.Series is not null => s.Series,
                _ => item.GetParent() ?? item
            };
        }

        return new FeedEntryDto
        {
            Id = r.Id,
            UserId = r.UserId,
            UserName = author?.Username ?? "Deleted user",
            UserHasImage = author?.ProfileImage is not null,
            ItemId = item.Id,
            ItemName = item.Name ?? r.Item.Name ?? string.Empty,
            ItemType = item.GetType().Name,
            SeriesName = (item as Episode)?.SeriesName ?? (item as Season)?.SeriesName,
            ProductionYear = item.ProductionYear,
            ImageItemId = imageItem.HasImage(ImageType.Primary, 0) ? imageItem.Id : null,
            ImageTag = imageItem.HasImage(ImageType.Primary, 0) ? imageItem.GetImageInfo(ImageType.Primary, 0)?.DateModified.Ticks.ToString(CultureInfo.InvariantCulture) : null,
            Rating = r.Ducks,
            Title = r.Title,
            Body = r.Body,
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc,
            IsEdited = r.IsEdited
        };
    }

    private static void ParseCursor(string? cursor, out DateTime? createdUtc, out long? id)
    {
        createdUtc = null;
        id = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return;
        }

        var parts = cursor.Split('_');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId))
        {
            createdUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            id = rowId;
        }
    }
}
