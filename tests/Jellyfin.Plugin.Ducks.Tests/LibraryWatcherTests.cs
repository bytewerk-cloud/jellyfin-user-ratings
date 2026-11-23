using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ducks.Data;
using Jellyfin.Plugin.Ducks.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public sealed class LibraryWatcherTests : IDisposable
{
    private readonly ServiceFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Default_delay_is_thirty_seconds()
    {
        using var watcher = new LibraryWatcher(_f.Library.Object, _f.Service, NullLogger<LibraryWatcher>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(30), watcher.Delay);
    }

    [Fact]
    public async Task Item_added_triggers_a_debounced_relink()
    {
        var old = ServiceFixture.Movie("Old");
        _f.Service.Repository.Upsert(_f.Alice.Id, old.Id, 6, null, null, new ItemSnapshot { Type = "Movie", Path = old.Path });
        var replacement = _f.AddItem(ServiceFixture.Movie("New"));
        _f.Library.Setup(m => m.FindByPath(old.Path, null)).Returns(replacement);

        using var watcher = new LibraryWatcher(_f.Library.Object, _f.Service, NullLogger<LibraryWatcher>.Instance, TimeSpan.FromMilliseconds(20));
        await watcher.StartAsync(CancellationToken.None);
        _f.Library.Raise(m => m.ItemAdded += null, _f.Library.Object, new ItemChangeEventArgs { Item = replacement });
        _f.Library.Raise(m => m.ItemAdded += null, _f.Library.Object, new ItemChangeEventArgs { Item = replacement });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_f.Service.Repository.Get(_f.Alice.Id, replacement.Id) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(_f.Service.Repository.Get(_f.Alice.Id, replacement.Id));
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopped_watcher_ignores_events()
    {
        using var watcher = new LibraryWatcher(_f.Library.Object, _f.Service, NullLogger<LibraryWatcher>.Instance, TimeSpan.FromMilliseconds(1));
        await watcher.StartAsync(CancellationToken.None);
        await watcher.StopAsync(CancellationToken.None);
        _f.Library.Raise(m => m.ItemAdded += null, _f.Library.Object, new ItemChangeEventArgs { Item = ServiceFixture.Movie() });
        await Task.Delay(30);
        _f.Library.Verify(m => m.GetItemById(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void Run_logs_and_swallows_failures()
    {
        _f.Service.Repository.Upsert(_f.Alice.Id, Guid.NewGuid(), 6, null, null, new ItemSnapshot());
        _f.Library.Setup(m => m.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("db gone"));
        using var watcher = new LibraryWatcher(_f.Library.Object, _f.Service, NullLogger<LibraryWatcher>.Instance);
        watcher.Run();
    }
}
