using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Ducks.Data;

/// <summary>
/// Forward-only schema migrations. Append new steps; never edit or reorder existing ones.
/// </summary>
internal static class Migrations
{
    private static readonly IReadOnlyList<string> Steps =
    [
        // v1: initial schema
        """
        CREATE TABLE Reviews (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId         TEXT    NOT NULL,
            ItemId         TEXT    NOT NULL,
            HalfDucks      INTEGER NOT NULL CHECK (HalfDucks BETWEEN 0 AND 8),
            Title          TEXT    NULL,
            Body           TEXT    NULL,
            CreatedUtc     INTEGER NOT NULL,
            UpdatedUtc     INTEGER NOT NULL,
            ItemName       TEXT    NULL,
            ItemType       TEXT    NULL,
            ItemPath       TEXT    NULL,
            ProviderIds    TEXT    NULL,
            SeriesName     TEXT    NULL,
            ProductionYear INTEGER NULL,
            UNIQUE (UserId, ItemId)
        );
        CREATE INDEX IX_Reviews_ItemId ON Reviews (ItemId);
        CREATE INDEX IX_Reviews_Created ON Reviews (CreatedUtc DESC, Id DESC);

        CREATE TABLE ReviewHistory (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            ReviewId   INTEGER NOT NULL,
            UserId     TEXT    NOT NULL,
            ItemId     TEXT    NOT NULL,
            HalfDucks  INTEGER NOT NULL,
            Title      TEXT    NULL,
            Body       TEXT    NULL,
            CreatedUtc INTEGER NOT NULL,
            UpdatedUtc INTEGER NOT NULL,
            ChangeType TEXT    NOT NULL,
            ChangedUtc INTEGER NOT NULL
        );
        CREATE INDEX IX_ReviewHistory_ReviewId ON ReviewHistory (ReviewId);
        """
    ];

    public static int LatestVersion => Steps.Count;

    public static void Apply(DucksRepository repository)
    {
        using var connection = repository.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL, AppliedUtc INTEGER NOT NULL)";
            create.ExecuteNonQuery();
        }

        int current;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion";
            current = Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (current > Steps.Count)
        {
            throw new InvalidOperationException(
                $"Ducks database schema v{current} is newer than this plugin supports (v{Steps.Count}). Update the plugin; the database was not modified.");
        }

        for (var v = current + 1; v <= Steps.Count; v++)
        {
            using var tx = connection.BeginTransaction();
            using var step = connection.CreateCommand();
            step.Transaction = tx;
            step.CommandText = Steps[v - 1];
            step.ExecuteNonQuery();
            using var mark = connection.CreateCommand();
            mark.Transaction = tx;
            mark.CommandText = "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ($v, $now)";
            mark.Parameters.AddWithValue("$v", v);
            mark.Parameters.AddWithValue("$now", DucksRepository.ToUnixMs(DateTime.UtcNow));
            mark.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
