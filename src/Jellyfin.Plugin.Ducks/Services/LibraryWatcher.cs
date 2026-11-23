using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ducks.Services;

/// <summary>
/// Re-links orphaned ratings shortly after the library gains items (a moved file shows up as remove + add).
/// Debounced so a full library scan triggers one pass, not thousands.
/// </summary>
public sealed class LibraryWatcher : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly DucksService _service;
    private readonly ILogger<LibraryWatcher> _logger;
    private readonly Timer _timer;

    public LibraryWatcher(ILibraryManager libraryManager, DucksService service, ILogger<LibraryWatcher> logger)
        : this(libraryManager, service, logger, TimeSpan.FromSeconds(30))
    {
    }

    internal LibraryWatcher(ILibraryManager libraryManager, DucksService service, ILogger<LibraryWatcher> logger, TimeSpan delay)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
        Delay = delay;
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal TimeSpan Delay { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    internal void Run()
    {
        try
        {
            _service.RelinkOrphans();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ducks: re-linking orphaned ratings failed");
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Each new item pushes the run back, so it happens once the scan settles.
        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }
}
