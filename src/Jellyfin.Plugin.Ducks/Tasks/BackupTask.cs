using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ducks.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Ducks.Tasks;

/// <summary>
/// Daily copy of the ratings database (Dashboard → Scheduled Tasks → Ducks).
/// </summary>
public class BackupTask : IScheduledTask
{
    private readonly DucksService _service;

    public BackupTask(DucksService service)
    {
        _service = service;
    }

    public string Name => "Back up ratings";

    public string Key => "DucksBackup";

    public string Description => "Copies the Ducks ratings database to data/ducks/backups and prunes old copies.";

    public string Category => "Ducks";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _service.CreateBackup(DateTime.UtcNow, DucksService.Config.BackupsToKeep);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
        ];
    }
}
