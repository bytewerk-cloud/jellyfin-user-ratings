using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.Ducks.Api;
using Jellyfin.Plugin.Ducks.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly ServiceFixture _f = new();

    public void Dispose() => _f.Dispose();

    private DucksController As(Guid? userId)
    {
        var claims = userId is null ? Array.Empty<Claim>() : [new Claim("Jellyfin-UserId", userId.Value.ToString())];
        return new DucksController(_f.Service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };
    }

    [Fact]
    public void Ping_reports_plugin()
    {
        Assert.IsType<OkObjectResult>(As(_f.Alice.Id).Ping().Result);
    }

    [Fact]
    public void Missing_user_claim_is_rejected()
    {
        Assert.Throws<UnauthorizedAccessException>(() => As(null).GetFeed(null));
    }

    [Fact]
    public void Get_returns_404_for_invisible_item_and_data_for_visible()
    {
        var movie = _f.AddItem(ServiceFixture.Movie(), _f.Alice);
        Assert.IsType<NotFoundResult>(As(_f.Bob.Id).GetItem(movie.Id).Result);
        Assert.Equal(movie.Id, As(_f.Alice.Id).GetItem(movie.Id).Value!.ItemId);
    }

    [Fact]
    public void Save_handles_not_found_bad_request_and_success()
    {
        var movie = _f.AddItem(ServiceFixture.Movie(), _f.Alice);
        var controller = As(_f.Alice.Id);

        Assert.IsType<NotFoundResult>(As(_f.Bob.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 1 }).Result);
        Assert.IsType<BadRequestObjectResult>(controller.SaveItem(movie.Id, new SaveRatingRequest { Rating = 9 }).Result);
        Assert.Equal(1, controller.SaveItem(movie.Id, new SaveRatingRequest { Rating = 1 }).Value!.Mine!.Rating);
    }

    [Fact]
    public void Users_only_change_their_own_rating()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        As(_f.Alice.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 4 });
        As(_f.Bob.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 1 });
        As(_f.Bob.Id).DeleteItem(movie.Id);

        Assert.Equal(4, _f.Service.Repository.Get(_f.Alice.Id, movie.Id)!.Ducks);
        Assert.Null(_f.Service.Repository.Get(_f.Bob.Id, movie.Id));
    }

    [Fact]
    public void Delete_returns_404_for_invisible_item()
    {
        var movie = _f.AddItem(ServiceFixture.Movie(), _f.Alice);
        Assert.IsType<NotFoundResult>(As(_f.Bob.Id).DeleteItem(movie.Id).Result);
        Assert.Null(As(_f.Alice.Id).DeleteItem(movie.Id).Value!.Mine);
    }

    [Fact]
    public void Aggregates_and_feed_pass_through()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        As(_f.Alice.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 3 });

        Assert.Single(As(_f.Bob.Id).GetAggregates(new AggregatesRequest { ItemIds = [movie.Id] }).Value!);
        Assert.Single(As(_f.Bob.Id).GetFeed(null, 5).Value!.Items);
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        As(_f.Alice.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 3, Title = "t" });
        var export = (IReadOnlyList<Review>)((OkObjectResult)As(_f.Alice.Id).Export().Result!).Value!;
        _f.Service.Repository.Delete(_f.Alice.Id, movie.Id);

        var result = (OkObjectResult)As(_f.Alice.Id).Import([.. export]).Result!;

        Assert.Contains("Imported = 1", result.Value!.ToString(), StringComparison.Ordinal);
        Assert.Equal("t", _f.Service.Repository.Get(_f.Alice.Id, movie.Id)!.Title);
    }

    [Fact]
    public void Status_reports_database_count_and_backups()
    {
        var movie = _f.AddItem(ServiceFixture.Movie());
        As(_f.Alice.Id).SaveItem(movie.Id, new SaveRatingRequest { Rating = 3 });

        var before = As(_f.Alice.Id).Status().Value!;
        Assert.Equal(1, before.RatingCount);
        Assert.Null(before.LatestBackup);
        Assert.EndsWith("ducks.db", before.DatabasePath, StringComparison.Ordinal);

        _f.Service.CreateBackup(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 7);
        _f.Service.CreateBackup(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), 7);
        Assert.Equal("ducks-20260502.db", As(_f.Alice.Id).Status().Value!.LatestBackup);
        Assert.True(_f.Service.GetStatus(true).WebInjectionActive);
    }

    [Fact]
    public void Import_rejects_out_of_range_ratings()
    {
        var bad = new List<Review> { new() { UserId = Guid.NewGuid(), ItemId = Guid.NewGuid(), HalfDucks = 9 } };
        Assert.IsType<BadRequestObjectResult>(As(_f.Alice.Id).Import(bad).Result);
        Assert.Empty(_f.Service.Repository.GetAll());
    }
}
