using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ducks.Data;
using Jellyfin.Plugin.Ducks.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public sealed class DurabilityTests : IDisposable
{
    private readonly ServiceFixture _f = new();

    public void Dispose() => _f.Dispose();

    private string BackupDir => Path.Combine(_f.Service.DataDirectory, "backups");

    [Fact]
    public void Backup_writes_daily_copy_and_keeps_newest_n()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        _f.Service.Repository.Upsert(_f.Alice.Id, movie.Id, 6, "keep", null, new ItemSnapshot());
        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            _f.Service.CreateBackup(day.AddDays(i), 3);
        }

        var path = _f.Service.CreateBackup(day.AddDays(4), 3); // same day again: replaced, not duplicated

        var names = Directory.GetFiles(BackupDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(["ducks-20260303.db", "ducks-20260304.db", "ducks-20260305.db"], names);
        Assert.Equal("keep", new DucksRepository(path).Get(_f.Alice.Id, movie.Id)!.Title);
    }

    [Fact]
    public void Backup_always_keeps_at_least_one()
    {
        _f.Service.CreateBackup(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), 0);
        Assert.Single(Directory.GetFiles(BackupDir));
    }

    [Fact]
    public async Task Backup_task_runs_and_reports_progress()
    {
        var task = new BackupTask(_f.Service);
        var progress = new Recorder();
        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Single(Directory.GetFiles(BackupDir, "ducks-*.db"));
        Assert.Equal(100, progress.Last);
        Assert.Equal("DucksBackup", task.Key);
        Assert.Equal("Ducks", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Name + task.Description));
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
    }

    [Fact]
    public async Task Relink_task_runs_at_startup_and_daily()
    {
        var task = new RelinkTask(_f.Service);
        var progress = new Recorder();
        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(100, progress.Last);
        Assert.Equal("DucksRelink", task.Key);
        Assert.Equal("Ducks", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Name + task.Description));
        Assert.Equal([TaskTriggerInfoType.StartupTrigger, TaskTriggerInfoType.DailyTrigger], task.GetDefaultTriggers().Select(t => t.Type));
    }

    [Fact]
    public void Upgrading_schema_first_writes_a_full_copy_and_keeps_data()
    {
        var dir = Path.Combine(_f.DataPath, "upgrade");
        var dbPath = Path.Combine(dir, "ducks.db");
        var repo = new DucksRepository(dbPath);
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        repo.Upsert(user, item, 5, "before upgrade", null, new ItemSnapshot());

        var v2 = new[] { "SELECT 1", "ALTER TABLE Reviews ADD COLUMN Spoiler INTEGER NOT NULL DEFAULT 0" };
        var v1Only = new[] { "SELECT 1" };
        Migrations.Apply(repo, v2);

        var backup = Path.Combine(dir, "backups", "ducks.pre-v2.db");
        Assert.True(File.Exists(backup));
        Assert.Equal("before upgrade", repo.Get(user, item)!.Title);
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection("Data Source=" + backup))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }

        // Already at latest: no new copy.
        File.Delete(backup);
        Migrations.Apply(repo, v2);
        Assert.False(File.Exists(backup));
        Assert.Throws<InvalidOperationException>(() => Migrations.Apply(repo, v1Only));
    }

    [Fact]
    public void Fresh_database_gets_no_pre_migration_copy()
    {
        Assert.NotNull(_f.Service.Repository);
        Assert.False(Directory.Exists(BackupDir));
    }

    private sealed class Recorder : IProgress<double>
    {
        public double Last { get; private set; }

        public void Report(double value) => Last = value;
    }
}
