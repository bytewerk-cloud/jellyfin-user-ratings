using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Ducks.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Ducks.Tests;

/// <summary>
/// A DucksService wired to mocked Jellyfin managers and a temp database.
/// </summary>
public sealed class ServiceFixture : IDisposable
{
    public ServiceFixture()
    {
        Directory.CreateDirectory(DataPath);
        Paths.SetupGet(p => p.DataPath).Returns(DataPath);
        Alice = AddUser("alice");
        Bob = AddUser("bob");
        UserData.Setup(m => m.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem _) => new UserItemData { Key = "k" });
        Service = new DucksService(Library.Object, Users.Object, UserData.Object, Paths.Object, NullLogger<DucksService>.Instance);
    }

    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "ducks-svc-" + Guid.NewGuid().ToString("N"));

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IUserManager> Users { get; } = new();

    public Mock<IUserDataManager> UserData { get; } = new();

    public Mock<IApplicationPaths> Paths { get; } = new();

    public DucksService Service { get; }

    public User Alice { get; }

    public User Bob { get; }

    public User AddUser(string name)
    {
        var user = new User(name, "Default", "Default");
        Users.Setup(m => m.GetUserById(user.Id)).Returns(user);
        return user;
    }

    /// <summary>Adds an item visible to the given users (all known users when none given).</summary>
    public T AddItem<T>(T item, params User[] visibleTo)
        where T : BaseItem
    {
        if (item.Id == Guid.Empty)
        {
            item.Id = Guid.NewGuid();
        }

        Library.Setup(m => m.GetItemById(item.Id)).Returns(item);
        var users = visibleTo.Length == 0 ? new[] { Alice, Bob } : visibleTo;
        foreach (var u in users)
        {
            Library.Setup(m => m.GetItemById<BaseItem>(item.Id, u)).Returns(item);
        }

        return item;
    }

    public static Movie Movie(string name = "Movie", bool withImage = true)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = 2020, Path = "/media/" + name + ".mkv" };
        if (withImage)
        {
            movie.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/img/" + name + ".jpg", DateModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }];
        }

        return movie;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(DataPath, true);
    }
}
