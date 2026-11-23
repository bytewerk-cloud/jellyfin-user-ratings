using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ducks.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Ducks.Tasks;

/// <summary>
/// Re-attaches ratings to items whose Jellyfin id changed (library removed and re-added, files moved, etc.).
/// </summary>
public class RelinkTask : IScheduledTask
{
    private readonly DucksService _service;

    public RelinkTask(DucksService service)
    {
        _service = service;
    }

    public string Name => "Re-link orphaned ratings";

    public string Key => "DucksRelink";

    public string Description => "Finds ratings whose item no longer exists and moves them to the matching item by path or provider ids (IMDb, TMDb, TVDb).";

    public string Category => "Ducks";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _service.RelinkOrphans();
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }
        ];
    }
}
