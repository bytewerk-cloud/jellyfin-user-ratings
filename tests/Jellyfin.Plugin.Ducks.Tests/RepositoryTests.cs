using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ducks.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public sealed class RepositoryTests : IDisposable
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Movie = Guid.NewGuid();
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ducks-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DucksRepository _repo;

    public RepositoryTests()
    {
        _repo = new DucksRepository(Path.Combine(_dir, "ducks.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static ItemSnapshot Snap(string name = "Movie") => new() { Name = name, Type = "Movie", ProviderIdsJson = "{\"Imdb\":\"tt1\"}" };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 1)]
    [InlineData(3.5, 7)]
    [InlineData(4, 8)]
    public void ToHalfDucks_accepts_half_steps(double ducks, int expected)
        => Assert.Equal(expected, DucksRepository.ToHalfDucks(ducks));

    [Theory]
    [InlineData(-0.5)]
    [InlineData(4.5)]
    [InlineData(1.25)]
    [InlineData(double.NaN)]
    public void ToHalfDucks_rejects_invalid(double ducks)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DucksRepository.ToHalfDucks(ducks));

    [Fact]
    public void Rating_without_review_text_is_stored()
    {
        var saved = _repo.Upsert(Alice, Movie, 7, null, "  ", Snap(), T0);
        Assert.Equal(3.5, saved.Ducks);
        Assert.Null(saved.Title);
        Assert.Null(saved.Body);
        Assert.False(saved.IsEdited);
    }

    [Fact]
    public void Zero_ducks_is_a_real_rating()
    {
        _repo.Upsert(Alice, Movie, 0, null, null, Snap(), T0);
        Assert.Equal(0, _repo.Get(Alice, Movie)!.HalfDucks);
        Assert.Equal(1, _repo.GetAggregates([Movie])[Movie].Count);
    }

    [Fact]
    public void Editing_keeps_created_time_marks_edited_and_writes_history()
    {
        var first = _repo.Upsert(Alice, Movie, 4, "Meh", "ok", Snap(), T0);
        var second = _repo.Upsert(Alice, Movie, 8, "Great", "loved it", Snap(), T0.AddDays(1));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(T0, second.CreatedUtc);
        Assert.Equal(T0.AddDays(1), second.UpdatedUtc);
        Assert.True(second.IsEdited);
        Assert.Equal("Great", second.Title);
        Assert.Equal(1, _repo.CountHistory());
    }

    [Fact]
    public void Saving_identical_content_does_not_mark_edited()
    {
        _repo.Upsert(Alice, Movie, 4, "t", "b", Snap(), T0);
        var again = _repo.Upsert(Alice, Movie, 4, "t", "b", Snap(), T0.AddDays(1));
        Assert.False(again.IsEdited);
        Assert.Equal(0, _repo.CountHistory());
    }

    [Fact]
    public void Aggregate_averages_all_users_per_item()
    {
        _repo.Upsert(Alice, Movie, 8, null, null, Snap(), T0);
        _repo.Upsert(Bob, Movie, 5, null, null, Snap(), T0);
        _repo.Upsert(Alice, Show, 2, null, null, Snap("Show"), T0);

        var agg = _repo.GetAggregates([Movie, Show, Guid.NewGuid()]);

        Assert.Equal(2, agg.Count);
        Assert.Equal(3.25, agg[Movie].Average);
        Assert.Equal(2, agg[Movie].Count);
        Assert.Equal(1.0, agg[Show].Average);
    }

    [Fact]
    public void Aggregates_handle_more_ids_than_sqlite_parameter_limit()
    {
        _repo.Upsert(Alice, Movie, 6, null, null, Snap(), T0);
        var ids = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).Append(Movie).ToList();
        Assert.Single(_repo.GetAggregates(ids));
    }

    [Fact]
    public void Delete_removes_rating_but_keeps_history_copy()
    {
        _repo.Upsert(Alice, Movie, 6, "t", "b", Snap(), T0);
        Assert.True(_repo.Delete(Alice, Movie));
        Assert.Null(_repo.Get(Alice, Movie));
        Assert.Equal(1, _repo.CountHistory());
        Assert.False(_repo.Delete(Alice, Movie));
    }

    [Fact]
    public void Feed_is_newest_first_and_pages_with_cursor()
    {
        for (var i = 0; i < 5; i++)
        {
            _repo.Upsert(Guid.NewGuid(), Movie, i, "r" + i, null, Snap(), T0.AddMinutes(i));
        }

        var page1 = _repo.GetFeedPage(null, null, 2);
        Assert.Equal(["r4", "r3"], page1.Select(r => r.Title));
        var last = page1[^1];
        var page2 = _repo.GetFeedPage(last.CreatedUtc, last.Id, 2);
        Assert.Equal(["r2", "r1"], page2.Select(r => r.Title));
    }

    [Fact]
    public void Feed_cursor_handles_identical_timestamps()
    {
        for (var i = 0; i < 4; i++)
        {
            _repo.Upsert(Guid.NewGuid(), Movie, i, "r" + i, null, Snap(), T0);
        }

        var page1 = _repo.GetFeedPage(null, null, 2);
        var page2 = _repo.GetFeedPage(page1[^1].CreatedUtc, page1[^1].Id, 10);
        Assert.Equal(4, page1.Concat(page2).Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Data_survives_reopening_the_database()
    {
        _repo.Upsert(Alice, Movie, 7, "Keep me", "please", Snap(), T0);
        SqliteConnection.ClearAllPools();

        var reopened = new DucksRepository(Path.Combine(_dir, "ducks.db"));
        Assert.Equal("Keep me", reopened.Get(Alice, Movie)!.Title);
    }

    [Fact]
    public void Newer_schema_than_plugin_is_refused_without_touching_data()
    {
        _repo.Upsert(Alice, Movie, 7, null, null, Snap(), T0);
        using (var c = new SqliteConnection("Data Source=" + _repo.DatabasePath))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO SchemaVersion VALUES (999, 0)";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(() => new DucksRepository(_repo.DatabasePath));
    }

    [Fact]
    public void Backup_produces_a_readable_copy()
    {
        _repo.Upsert(Alice, Movie, 7, "Backed up", null, Snap(), T0);
        var backup = Path.Combine(_dir, "backups", "copy.db");
        _repo.BackupTo(backup);

        var copy = new DucksRepository(backup);
        Assert.Equal("Backed up", copy.Get(Alice, Movie)!.Title);
    }

    [Fact]
    public void Relink_moves_reviews_to_new_item_id()
    {
        var newMovie = Guid.NewGuid();
        _repo.Upsert(Alice, Movie, 7, null, null, Snap(), T0);
        _repo.Upsert(Bob, Movie, 3, null, null, Snap(), T0);

        Assert.Equal(2, _repo.Relink(Movie, newMovie));
        Assert.Empty(_repo.GetAggregates([Movie]));
        Assert.Equal(2, _repo.GetAggregates([newMovie])[newMovie].Count);
    }

    [Fact]
    public void Relink_conflict_keeps_newest_and_archives_other()
    {
        var newMovie = Guid.NewGuid();
        _repo.Upsert(Alice, Movie, 2, "old", null, Snap(), T0);
        _repo.Upsert(Alice, newMovie, 8, "new", null, Snap(), T0.AddDays(1));

        _repo.Relink(Movie, newMovie);

        Assert.Equal("new", _repo.Get(Alice, newMovie)!.Title);
        Assert.Null(_repo.Get(Alice, Movie));
        Assert.Equal(1, _repo.CountHistory());
    }

    [Fact]
    public void Import_restores_export_and_skips_older_rows()
    {
        _repo.Upsert(Alice, Movie, 7, "a", null, Snap(), T0);
        _repo.Upsert(Bob, Movie, 3, "b", null, Snap(), T0);
        var export = _repo.GetAll();

        var other = new DucksRepository(Path.Combine(_dir, "other.db"));
        other.Upsert(Alice, Movie, 1, "newer local", null, Snap(), T0.AddDays(5));

        Assert.Equal(1, other.Import(export));
        Assert.Equal("newer local", other.Get(Alice, Movie)!.Title);
        Assert.Equal("b", other.Get(Bob, Movie)!.Title);
    }
}
