using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Ducks.Api;
using Jellyfin.Plugin.Ducks.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public sealed class ServiceTests : IDisposable
{
    private readonly ServiceFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Database_lives_in_jellyfin_data_path_not_plugin_folder()
    {
        Assert.Equal(System.IO.Path.Combine(_f.DataPath, "ducks"), _f.Service.DataDirectory);
        Assert.StartsWith(_f.Service.DataDirectory, _f.Service.Repository.DatabasePath);
    }

    [Fact]
    public void GetVisibleItem_requires_known_user_and_access()
    {
        var movie = _f.AddItem(ServiceFixture.Movie(), _f.Alice);
        Assert.Same(movie, _f.Service.GetVisibleItem(_f.Alice.Id, movie.Id));
        Assert.Null(_f.Service.GetVisibleItem(_f.Bob.Id, movie.Id));
        Assert.Null(_f.Service.GetVisibleItem(Guid.NewGuid(), movie.Id));
    }

    [Fact]
    public void Folders_people_and_genres_are_not_ratable()
    {
        var folder = _f.AddItem(new Folder { Id = Guid.NewGuid() });
        var person = _f.AddItem(new Person { Id = Guid.NewGuid() });
        var series = _f.AddItem(new Series { Id = Guid.NewGuid() });

        Assert.Null(_f.Service.GetVisibleItem(_f.Alice.Id, folder.Id));
        Assert.Null(_f.Service.GetVisibleItem(_f.Alice.Id, person.Id));
        Assert.Same(series, _f.Service.GetVisibleItem(_f.Alice.Id, series.Id));
        Assert.True(Jellyfin.Plugin.Ducks.Services.DucksService.IsRatable(new BoxSet()));
    }

    [Fact]
    public void Unrated_item_has_no_mine_and_no_aggregate()
    {
        var dto = _f.Service.GetItemRating(_f.Alice.Id, Guid.NewGuid());
        Assert.Null(dto.Mine);
        Assert.Null(dto.Aggregate);
        Assert.Equal(100, dto.MaxTitleLength);
        Assert.Equal(2000, dto.MaxBodyLength);
    }

    [Fact]
    public void Save_stores_rating_and_mirrors_to_jellyfin_user_data()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        UserItemData? saved = null;
        _f.UserData.Setup(m => m.SaveUserData(_f.Alice, movie, It.IsAny<UserItemData>(), UserDataSaveReason.UpdateUserRating, It.IsAny<CancellationToken>()))
            .Callback((User _, BaseItem _, UserItemData d, UserDataSaveReason _, CancellationToken _) => saved = d);

        var dto = _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 3.5, Title = "  Good  ", Body = "Nice" });

        Assert.Equal(3.5, dto.Mine!.Rating);
        Assert.Equal("Good", dto.Mine.Title);
        Assert.Equal(3.5, dto.Aggregate!.Average);
        Assert.Equal(8.75, saved!.Rating);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4.5)]
    [InlineData(2.2)]
    public void Save_rejects_invalid_rating(double rating)
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        var ex = Assert.Throws<ArgumentException>(() => _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = rating }));
        Assert.Equal("Rating must be 0 to 4 ducks in steps of 0.5.", ex.Message);
    }

    [Fact]
    public void Save_rejects_too_long_title_and_body()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        Assert.Throws<ArgumentException>(() => _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 1, Title = new string('x', 101) }));
        Assert.Throws<ArgumentException>(() => _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 1, Body = new string('x', 2001) }));
        Assert.Null(_f.Service.Repository.Get(_f.Alice.Id, movie.Id));
    }

    [Fact]
    public void Mirror_failure_does_not_lose_the_rating()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.UserData.Setup(m => m.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("db locked"));

        var dto = _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 2 });
        Assert.Equal(2, dto.Mine!.Rating);
    }

    [Fact]
    public void Mirror_is_skipped_when_user_or_user_data_missing()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        var ghost = Guid.NewGuid();
        _f.Service.Save(ghost, movie, new SaveRatingRequest { Rating = 2 });

        _f.UserData.Setup(m => m.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((UserItemData?)null);
        _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 2 });

        _f.UserData.Verify(m => m.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, _f.Service.Repository.GetAggregates([movie.Id])[movie.Id].Count);
    }

    [Fact]
    public void Delete_removes_rating_and_clears_mirror()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 2 });
        UserItemData? saved = null;
        _f.UserData.Setup(m => m.SaveUserData(_f.Alice, movie, It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BaseItem _, UserItemData d, UserDataSaveReason _, CancellationToken _) => saved = d);

        var dto = _f.Service.Delete(_f.Alice.Id, movie);

        Assert.Null(dto.Mine);
        Assert.Null(saved!.Rating);
    }

    [Fact]
    public void Aggregates_are_keyed_by_jellyfin_id_format()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 4 });
        _f.Service.Save(_f.Bob.Id, movie, new SaveRatingRequest { Rating = 1 });

        var agg = _f.Service.GetAggregates([movie.Id, Guid.NewGuid()]);

        var entry = Assert.Single(agg);
        Assert.Equal(movie.Id.ToString("N"), entry.Key);
        Assert.Equal(2.5, entry.Value.Average);
        Assert.Equal(2, entry.Value.Count);
    }

    [Fact]
    public void Feed_rejects_unknown_viewer()
    {
        Assert.Throws<ArgumentException>(() => _f.Service.GetFeed(Guid.NewGuid(), null, 10));
    }

    [Fact]
    public void Feed_is_newest_first_with_author_and_item_details()
    {
        var a = _f.AddItem(ServiceFixture.Movie("A"));
        var b = _f.AddItem(ServiceFixture.Movie("B"));
        _f.Service.Repository.Upsert(_f.Alice.Id, a.Id, 8, "Loved it", "Body", new ItemSnapshot(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _f.Service.Repository.Upsert(_f.Bob.Id, b.Id, 3, null, null, new ItemSnapshot(), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var page = _f.Service.GetFeed(_f.Alice.Id, null, 10);

        Assert.Equal(["B", "A"], page.Items.Select(i => i.ItemName));
        var first = page.Items[0];
        Assert.Equal("bob", first.UserName);
        Assert.False(first.UserHasImage);
        Assert.Equal(1.5, first.Rating);
        Assert.Equal(b.Id, first.ImageItemId);
        Assert.NotNull(first.ImageTag);
        Assert.Equal("Movie", first.ItemType);
        Assert.Equal(2020, first.ProductionYear);
        Assert.Equal("Loved it", page.Items[1].Title);
        Assert.Null(page.Next);
    }

    [Fact]
    public void Feed_hides_items_the_viewer_cannot_see_and_still_fills_the_page()
    {
        var hidden = _f.AddItem(ServiceFixture.Movie("Hidden"), _f.Bob);
        var shown = _f.AddItem(ServiceFixture.Movie("Shown"));
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _f.Service.Repository.Upsert(_f.Bob.Id, shown.Id, 4, null, null, new ItemSnapshot(), t);
        for (var i = 0; i < 6; i++)
        {
            _f.Service.Repository.Upsert(_f.AddUser("u" + i).Id, hidden.Id, 4, null, null, new ItemSnapshot(), t.AddMinutes(i + 1));
        }

        var page = _f.Service.GetFeed(_f.Alice.Id, null, 1);

        Assert.Equal("Shown", Assert.Single(page.Items).ItemName);
    }

    [Fact]
    public void Feed_pages_with_cursor_until_exhausted()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            _f.Service.Repository.Upsert(_f.AddUser("p" + i).Id, movie.Id, i, "r" + i, null, new ItemSnapshot(), t.AddMinutes(i));
        }

        var titles = new List<string?>();
        string? cursor = null;
        var calls = 0;
        do
        {
            var page = _f.Service.GetFeed(_f.Alice.Id, cursor, 2);
            titles.AddRange(page.Items.Select(i => i.Title));
            cursor = page.Next;
            calls++;
        }
        while (cursor is not null && calls < 10);

        Assert.Equal(["r4", "r3", "r2", "r1", "r0"], titles);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1_x")]
    [InlineData("")]
    public void Feed_ignores_malformed_cursor(string cursor)
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Repository.Upsert(_f.Bob.Id, movie.Id, 4, null, null, new ItemSnapshot());
        Assert.Single(_f.Service.GetFeed(_f.Alice.Id, cursor, 10).Items);
    }

    [Fact]
    public void Feed_limit_is_clamped()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Repository.Upsert(_f.Bob.Id, movie.Id, 4, null, null, new ItemSnapshot());
        Assert.Single(_f.Service.GetFeed(_f.Alice.Id, null, 0).Items);
    }

    [Fact]
    public void Feed_shows_deleted_users_and_profile_images()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Alice.ProfileImage = new ImageInfo("/img/alice.png");
        _f.Service.Repository.Upsert(Guid.NewGuid(), movie.Id, 4, null, null, new ItemSnapshot(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _f.Service.Repository.Upsert(_f.Alice.Id, movie.Id, 4, null, null, new ItemSnapshot(), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var items = _f.Service.GetFeed(_f.Alice.Id, null, 10).Items;

        Assert.True(items[0].UserHasImage);
        Assert.Equal("Deleted user", items[1].UserName);
    }

    [Fact]
    public void Feed_uses_series_image_for_episodes_without_their_own()
    {
        BaseItem.LibraryManager = _f.Library.Object;
        var series = _f.AddItem(new Series { Id = Guid.NewGuid(), Name = "Show", ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/s.jpg" }] });
        var season = _f.AddItem(new Season { Id = Guid.NewGuid(), Name = "Season 1", SeriesId = series.Id, SeriesName = "Show", ParentId = series.Id });
        var episode = _f.AddItem(new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesId = series.Id, SeriesName = "Show", ParentId = season.Id });
        _f.Service.Repository.Upsert(_f.Bob.Id, episode.Id, 4, null, null, new ItemSnapshot(), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _f.Service.Repository.Upsert(_f.Bob.Id, season.Id, 4, null, null, new ItemSnapshot(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var items = _f.Service.GetFeed(_f.Alice.Id, null, 10).Items;

        Assert.All(items, i => Assert.Equal(series.Id, i.ImageItemId));
        Assert.All(items, i => Assert.Equal("Show", i.SeriesName));
    }

    [Fact]
    public void Feed_entry_without_any_image_has_no_image_id()
    {
        BaseItem.LibraryManager = _f.Library.Object;
        var movie = _f.AddItem(ServiceFixture.Movie(withImage: false));
        _f.Service.Repository.Upsert(_f.Bob.Id, movie.Id, 4, null, null, new ItemSnapshot());

        var entry = Assert.Single(_f.Service.GetFeed(_f.Alice.Id, null, 10).Items);

        Assert.Null(entry.ImageItemId);
        Assert.Null(entry.ImageTag);
    }

    [Fact]
    public void Save_snapshots_item_details_for_relinking()
    {
        var episode = _f.AddItem(new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesName = "Show", Path = "/tv/pilot.mkv", ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "42" } });
        var season = _f.AddItem(new Season { Id = Guid.NewGuid(), Name = "S1", SeriesName = "Show" });
        _f.Service.Save(_f.Alice.Id, episode, new SaveRatingRequest { Rating = 1 });
        _f.Service.Save(_f.Alice.Id, season, new SaveRatingRequest { Rating = 1 });

        var snap = _f.Service.Repository.Get(_f.Alice.Id, episode.Id)!.Item;
        Assert.Equal("Episode", snap.Type);
        Assert.Equal("Show", snap.SeriesName);
        Assert.Equal("/tv/pilot.mkv", snap.Path);
        Assert.Contains("\"Tvdb\":\"42\"", snap.ProviderIdsJson);
        Assert.Equal("Show", _f.Service.Repository.Get(_f.Alice.Id, season.Id)!.Item.SeriesName);
    }

    [Fact]
    public void Relink_skips_items_that_still_exist()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Save(_f.Alice.Id, movie, new SaveRatingRequest { Rating = 1 });
        Assert.Equal(0, _f.Service.RelinkOrphans());
    }

    [Fact]
    public void Relink_matches_orphan_by_path()
    {
        var old = ServiceFixture.Movie("Old");
        _f.Service.Repository.Upsert(_f.Alice.Id, old.Id, 6, null, null, new ItemSnapshot { Type = "Movie", Path = old.Path });
        var replacement = _f.AddItem(ServiceFixture.Movie("New"));
        _f.Library.Setup(m => m.FindByPath(old.Path, null)).Returns(replacement);

        Assert.Equal(1, _f.Service.RelinkOrphans());
        Assert.NotNull(_f.Service.Repository.Get(_f.Alice.Id, replacement.Id));
    }

    [Fact]
    public void Relink_ignores_path_match_of_different_type_and_falls_back_to_provider_ids()
    {
        var oldId = Guid.NewGuid();
        _f.Service.Repository.Upsert(_f.Alice.Id, oldId, 6, null, null, new ItemSnapshot { Type = "Movie", Path = "/m.mkv", ProviderIdsJson = "{\"Imdb\":\"tt1\"}" });
        _f.Library.Setup(m => m.FindByPath("/m.mkv", null)).Returns(new Folder { Id = Guid.NewGuid() });
        var replacement = _f.AddItem(ServiceFixture.Movie("New"));
        _f.Library.Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Movie) && q.HasAnyProviderId!["Imdb"] == "tt1")))
            .Returns([replacement]);

        Assert.Equal(1, _f.Service.RelinkOrphans());
        Assert.NotNull(_f.Service.Repository.Get(_f.Alice.Id, replacement.Id));
    }

    [Fact]
    public void Relink_leaves_ambiguous_or_unmatchable_orphans_alone()
    {
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot { Type = "Movie", ProviderIdsJson = "{\"Imdb\":\"tt2\"}" });
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot { Type = "Movie", ProviderIdsJson = "{}" });
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot { Type = "NotAKind", ProviderIdsJson = "{\"Imdb\":\"tt3\"}" });
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot { Type = "Movie" });
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot { Type = "Movie", ProviderIdsJson = "null" });
        _f.Library.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([ServiceFixture.Movie("x"), ServiceFixture.Movie("y")]);

        Assert.Equal(0, _f.Service.RelinkOrphans());
        Assert.Equal(5, _f.Service.Repository.GetAll().Count);
    }
}
