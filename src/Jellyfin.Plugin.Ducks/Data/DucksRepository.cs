using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Ducks.Data;

/// <summary>
/// SQLite storage for ratings and reviews. The database lives in Jellyfin's data directory,
/// outside the plugin folder, so it survives server and plugin updates.
/// Nothing is ever hard-deleted without a copy in ReviewHistory.
/// </summary>
public sealed class DucksRepository
{
    public const int MaxHalfDucks = 8;

    private readonly string _connectionString;

    public DucksRepository(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        Migrations.Apply(this);
    }

    public string DatabasePath { get; }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public static int ToHalfDucks(double ducks)
    {
        var half = ducks * 2;
        if (double.IsNaN(half) || half < 0 || half > MaxHalfDucks || Math.Abs(half - Math.Round(half)) > 1e-9)
        {
            throw new ArgumentOutOfRangeException(nameof(ducks), ducks, "Rating must be 0 to 4 in steps of 0.5.");
        }

        return (int)Math.Round(half);
    }

    public Review? Get(Guid userId, Guid itemId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE UserId = $u AND ItemId = $i";
        cmd.Parameters.AddWithValue("$u", Key(userId));
        cmd.Parameters.AddWithValue("$i", Key(itemId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadReview(reader) : null;
    }

    /// <summary>
    /// Creates or updates the user's review of an item. The previous version is copied to history.
    /// Empty title/body are stored as null.
    /// </summary>
    public Review Upsert(Guid userId, Guid itemId, int halfDucks, string? title, string? body, ItemSnapshot item, DateTime? nowUtc = null)
    {
        if (halfDucks < 0 || halfDucks > MaxHalfDucks)
        {
            throw new ArgumentOutOfRangeException(nameof(halfDucks));
        }

        title = Normalize(title);
        body = Normalize(body);
        var now = nowUtc ?? DateTime.UtcNow;

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        var existing = GetInTransaction(connection, tx, userId, itemId);
        if (existing is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Reviews (UserId, ItemId, HalfDucks, Title, Body, CreatedUtc, UpdatedUtc,
                                     ItemName, ItemType, ItemPath, ProviderIds, SeriesName, ProductionYear)
                VALUES ($u, $i, $r, $t, $b, $now, $now, $name, $type, $path, $pids, $series, $year)
                """;
            AddReviewParams(insert, userId, itemId, halfDucks, title, body, item);
            insert.Parameters.AddWithValue("$now", ToUnixMs(now));
            insert.ExecuteNonQuery();
        }
        else
        {
            var unchanged = existing.HalfDucks == halfDucks && existing.Title == title && existing.Body == body;
            if (!unchanged)
            {
                CopyToHistory(connection, tx, existing, "edited", now);
            }

            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE Reviews SET HalfDucks = $r, Title = $t, Body = $b,
                       UpdatedUtc = CASE WHEN $changed THEN $now ELSE UpdatedUtc END,
                       ItemName = $name, ItemType = $type, ItemPath = $path, ProviderIds = $pids,
                       SeriesName = $series, ProductionYear = $year
                WHERE UserId = $u AND ItemId = $i
                """;
            AddReviewParams(update, userId, itemId, halfDucks, title, body, item);
            update.Parameters.AddWithValue("$now", ToUnixMs(now));
            update.Parameters.AddWithValue("$changed", unchanged ? 0 : 1);
            update.ExecuteNonQuery();
        }

        var saved = GetInTransaction(connection, tx, userId, itemId)!;
        tx.Commit();
        return saved;
    }

    /// <summary>
    /// Removes the user's rating. A copy is kept in history.
    /// </summary>
    public bool Delete(Guid userId, Guid itemId, DateTime? nowUtc = null)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var existing = GetInTransaction(connection, tx, userId, itemId);
        if (existing is null)
        {
            return false;
        }

        CopyToHistory(connection, tx, existing, "deleted", nowUtc ?? DateTime.UtcNow);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM Reviews WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", existing.Id);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public IReadOnlyDictionary<Guid, Aggregate> GetAggregates(IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.Distinct().ToList();
        var result = new Dictionary<Guid, Aggregate>();
        if (ids.Count == 0)
        {
            return result;
        }

        using var connection = Open();

        // SQLite limits bound parameters; chunk to stay well below it.
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = connection.CreateCommand();
            var names = new List<string>(chunk.Length);
            for (var n = 0; n < chunk.Length; n++)
            {
                var name = "$p" + n.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                cmd.Parameters.AddWithValue(name, Key(chunk[n]));
            }

            cmd.CommandText = "SELECT ItemId, AVG(HalfDucks), COUNT(*) FROM Reviews WHERE ItemId IN ("
                + string.Join(',', names) + ") GROUP BY ItemId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                result[id] = new Aggregate(id, reader.GetDouble(1) / 2.0, reader.GetInt32(2));
            }
        }

        return result;
    }

    /// <summary>
    /// Reviews newest first (by creation time). Used for the feed; caller filters by visibility.
    /// </summary>
    public IReadOnlyList<Review> GetFeedPage(DateTime? beforeCreatedUtc, long? beforeId, int limit)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        if (beforeCreatedUtc is null)
        {
            cmd.CommandText = SelectColumns + " ORDER BY CreatedUtc DESC, Id DESC LIMIT $limit";
        }
        else
        {
            cmd.CommandText = SelectColumns
                + " WHERE CreatedUtc < $c OR (CreatedUtc = $c AND Id < $id) ORDER BY CreatedUtc DESC, Id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$c", ToUnixMs(beforeCreatedUtc.Value));
            cmd.Parameters.AddWithValue("$id", beforeId ?? long.MaxValue);
        }

        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public IReadOnlyList<Review> GetAll()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectColumns + " ORDER BY Id";
        return ReadAll(cmd);
    }

    /// <summary>
    /// Moves all reviews from an item id that no longer exists to its replacement.
    /// If a user already rated the new item, their newer row wins and the older one goes to history.
    /// </summary>
    public int Relink(Guid oldItemId, Guid newItemId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var moved = 0;
        foreach (var old in ReadAllInTransaction(connection, tx, SelectColumns + " WHERE ItemId = $i", Key(oldItemId)))
        {
            var clash = GetInTransaction(connection, tx, old.UserId, newItemId);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            if (clash is null)
            {
                cmd.CommandText = "UPDATE Reviews SET ItemId = $new WHERE Id = $id";
                cmd.Parameters.AddWithValue("$new", Key(newItemId));
                cmd.Parameters.AddWithValue("$id", old.Id);
                cmd.ExecuteNonQuery();
                moved++;
            }
            else
            {
                var loser = clash.UpdatedUtc >= old.UpdatedUtc ? old : clash;
                var winner = ReferenceEquals(loser, old) ? clash : old;
                CopyToHistory(connection, tx, loser, "merged", now);
                cmd.CommandText = "DELETE FROM Reviews WHERE Id = $id";
                cmd.Parameters.AddWithValue("$id", loser.Id);
                cmd.ExecuteNonQuery();
                if (ReferenceEquals(winner, old))
                {
                    using var move = connection.CreateCommand();
                    move.Transaction = tx;
                    move.CommandText = "UPDATE Reviews SET ItemId = $new WHERE Id = $id";
                    move.Parameters.AddWithValue("$new", Key(newItemId));
                    move.Parameters.AddWithValue("$id", old.Id);
                    move.ExecuteNonQuery();
                    moved++;
                }
            }
        }

        tx.Commit();
        return moved;
    }

    public int CountHistory(Guid? reviewId = null)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = reviewId is null ? "SELECT COUNT(*) FROM ReviewHistory" : "SELECT COUNT(*) FROM ReviewHistory WHERE ReviewId = $id";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a consistent copy of the live database to <paramref name="destinationPath"/>.
    /// </summary>
    public void BackupTo(string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tmp = destinationPath + ".tmp";
        File.Delete(tmp);
        using (var source = Open())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        File.Move(tmp, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Inserts reviews from an export. Existing (user, item) pairs are kept unless the import is newer.
    /// Returns the number of rows written.
    /// </summary>
    public int Import(IEnumerable<Review> reviews)
    {
        var written = 0;
        foreach (var r in reviews)
        {
            var existing = Get(r.UserId, r.ItemId);
            if (existing is not null && existing.UpdatedUtc >= r.UpdatedUtc)
            {
                continue;
            }

            using var connection = Open();
            using var tx = connection.BeginTransaction();
            if (existing is not null)
            {
                CopyToHistory(connection, tx, existing, "imported-over", DateTime.UtcNow);
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Reviews (UserId, ItemId, HalfDucks, Title, Body, CreatedUtc, UpdatedUtc,
                                     ItemName, ItemType, ItemPath, ProviderIds, SeriesName, ProductionYear)
                VALUES ($u, $i, $r, $t, $b, $created, $updated, $name, $type, $path, $pids, $series, $year)
                ON CONFLICT(UserId, ItemId) DO UPDATE SET HalfDucks = excluded.HalfDucks, Title = excluded.Title,
                    Body = excluded.Body, CreatedUtc = excluded.CreatedUtc, UpdatedUtc = excluded.UpdatedUtc
                """;
            AddReviewParams(cmd, r.UserId, r.ItemId, r.HalfDucks, Normalize(r.Title), Normalize(r.Body), r.Item);
            cmd.Parameters.AddWithValue("$created", ToUnixMs(r.CreatedUtc));
            cmd.Parameters.AddWithValue("$updated", ToUnixMs(r.UpdatedUtc));
            cmd.ExecuteNonQuery();
            tx.Commit();
            written++;
        }

        return written;
    }

    internal static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    internal static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private const string SelectColumns = """
        SELECT Id, UserId, ItemId, HalfDucks, Title, Body, CreatedUtc, UpdatedUtc,
               ItemName, ItemType, ItemPath, ProviderIds, SeriesName, ProductionYear
        FROM Reviews
        """;

    private static void AddReviewParams(SqliteCommand cmd, Guid userId, Guid itemId, int halfDucks, string? title, string? body, ItemSnapshot item)
    {
        cmd.Parameters.AddWithValue("$u", Key(userId));
        cmd.Parameters.AddWithValue("$i", Key(itemId));
        cmd.Parameters.AddWithValue("$r", halfDucks);
        cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)item.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", (object?)item.Type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)item.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pids", (object?)item.ProviderIdsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$series", (object?)item.SeriesName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$year", (object?)item.ProductionYear ?? DBNull.Value);
    }

    private static Review? GetInTransaction(SqliteConnection connection, SqliteTransaction tx, Guid userId, Guid itemId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectColumns + " WHERE UserId = $u AND ItemId = $i";
        cmd.Parameters.AddWithValue("$u", Key(userId));
        cmd.Parameters.AddWithValue("$i", Key(itemId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadReview(reader) : null;
    }

    private static List<Review> ReadAllInTransaction(SqliteConnection connection, SqliteTransaction tx, string sql, string itemKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$i", itemKey);
        return ReadAll(cmd);
    }

    private static List<Review> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Review>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadReview(reader));
        }

        return list;
    }

    private static void CopyToHistory(SqliteConnection connection, SqliteTransaction tx, Review r, string change, DateTime now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ReviewHistory (ReviewId, UserId, ItemId, HalfDucks, Title, Body, CreatedUtc, UpdatedUtc, ChangeType, ChangedUtc)
            VALUES ($id, $u, $i, $r, $t, $b, $created, $updated, $change, $now)
            """;
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$u", Key(r.UserId));
        cmd.Parameters.AddWithValue("$i", Key(r.ItemId));
        cmd.Parameters.AddWithValue("$r", r.HalfDucks);
        cmd.Parameters.AddWithValue("$t", (object?)r.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", (object?)r.Body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ToUnixMs(r.CreatedUtc));
        cmd.Parameters.AddWithValue("$updated", ToUnixMs(r.UpdatedUtc));
        cmd.Parameters.AddWithValue("$change", change);
        cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
        cmd.ExecuteNonQuery();
    }

    private static Review ReadReview(SqliteDataReader r)
    {
        return new Review
        {
            Id = r.GetInt64(0),
            UserId = Guid.Parse(r.GetString(1)),
            ItemId = Guid.Parse(r.GetString(2)),
            HalfDucks = r.GetInt32(3),
            Title = r.IsDBNull(4) ? null : r.GetString(4),
            Body = r.IsDBNull(5) ? null : r.GetString(5),
            CreatedUtc = FromUnixMs(r.GetInt64(6)),
            UpdatedUtc = FromUnixMs(r.GetInt64(7)),
            Item = new ItemSnapshot
            {
                Name = r.IsDBNull(8) ? null : r.GetString(8),
                Type = r.IsDBNull(9) ? null : r.GetString(9),
                Path = r.IsDBNull(10) ? null : r.GetString(10),
                ProviderIdsJson = r.IsDBNull(11) ? null : r.GetString(11),
                SeriesName = r.IsDBNull(12) ? null : r.GetString(12),
                ProductionYear = r.IsDBNull(13) ? null : r.GetInt32(13)
            }
        };
    }
}
