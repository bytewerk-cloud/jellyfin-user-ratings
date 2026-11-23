using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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

    public static void Apply(DucksRepository repository) => Apply(repository, Steps);

    /// <summary>
    /// Applies pending steps. Before changing an existing database, a full copy is written to
    /// backups/ducks.pre-v{N}.db so a failed or unwanted upgrade can always be undone.
    /// </summary>
    internal static void Apply(DucksRepository repository, IReadOnlyList<string> steps)
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

        if (current > steps.Count)
        {
            throw new InvalidOperationException(
                $"Ducks database schema v{current} is newer than this plugin supports (v{steps.Count}). Update the plugin; the database was not modified.");
        }

        if (current > 0 && current < steps.Count)
        {
            var backup = Path.Combine(Path.GetDirectoryName(repository.DatabasePath)!, "backups", $"ducks.pre-v{steps.Count}.db");
            repository.BackupTo(backup);
        }

        for (var v = current + 1; v <= steps.Count; v++)
        {
            using var tx = connection.BeginTransaction();
            using var step = connection.CreateCommand();
            step.Transaction = tx;
            step.CommandText = steps[v - 1];
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
