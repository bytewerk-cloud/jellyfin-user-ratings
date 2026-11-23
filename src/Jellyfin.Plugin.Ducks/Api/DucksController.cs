using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Plugin.Ducks.Data;
using Jellyfin.Plugin.Ducks.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ducks.Api;

/// <summary>
/// REST endpoints for ratings and reviews. A user can only write their own rating.
/// </summary>
[ApiController]
[Route("Ducks")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class DucksController : ControllerBase
{
    // Same value as Jellyfin.Api.Constants.InternalClaimTypes.UserId (Jellyfin.Api is not referenceable from plugins).
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly DucksService _service;

    public DucksController(DucksService service)
    {
        _service = service;
    }

    [HttpGet("Ping")]
    public ActionResult<object> Ping()
    {
        return Ok(new { Plugin = "Ducks", Version = Plugin.Instance?.Version.ToString() });
    }

    /// <summary>My rating/review for the item plus the aggregate of all users.</summary>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemRatingDto> GetItem([FromRoute] Guid itemId)
    {
        var userId = CurrentUserId();
        if (_service.GetVisibleItem(userId, itemId) is null)
        {
            return NotFound();
        }

        return _service.GetItemRating(userId, itemId);
    }

    /// <summary>Create or replace my rating (title/body optional).</summary>
    [HttpPut("Items/{itemId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemRatingDto> SaveItem([FromRoute] Guid itemId, [FromBody] SaveRatingRequest request)
    {
        var userId = CurrentUserId();
        var item = _service.GetVisibleItem(userId, itemId);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            return _service.Save(userId, item, request);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>Remove my rating. A copy is kept in history.</summary>
    [HttpDelete("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemRatingDto> DeleteItem([FromRoute] Guid itemId)
    {
        var userId = CurrentUserId();
        var item = _service.GetVisibleItem(userId, itemId);
        if (item is null)
        {
            return NotFound();
        }

        return _service.Delete(userId, item);
    }

    /// <summary>Aggregate ratings for many items at once (card badges). Items without ratings are omitted.</summary>
    [HttpPost("Aggregates")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, AggregateDto>> GetAggregates([FromBody] AggregatesRequest request)
    {
        return _service.GetAggregates(request.ItemIds);
    }

    /// <summary>All users' ratings and reviews, newest first.</summary>
    [HttpGet("Feed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FeedPageDto> GetFeed([FromQuery] string? cursor, [FromQuery] int limit = 20)
    {
        return _service.GetFeed(CurrentUserId(), cursor, limit);
    }

    /// <summary>Admin: health of the plugin (web injection, database, backups).</summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusDto> Status()
    {
        return _service.GetStatus(Injection.WebInjectionService.IsRegistered);
    }

    /// <summary>Admin: full export of all current ratings (JSON). History is kept in the database only.</summary>
    [HttpGet("Export")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Review>> Export()
    {
        return Ok(_service.Repository.GetAll());
    }

    /// <summary>Admin: import an export. Rows newer than the existing ones win; older versions go to history.</summary>
    [HttpPost("Import")]
    [Authorize(Policy = "RequiresElevation")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Import([FromBody] List<Review> reviews)
    {
        foreach (var r in reviews.Where(r => r.HalfDucks is < 0 or > DucksRepository.MaxHalfDucks))
        {
            return BadRequest(new { Error = $"Invalid rating for item {r.ItemId}" });
        }

        return Ok(new { Imported = _service.Repository.Import(reviews) });
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirstValue(UserIdClaim);
        return Guid.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException("No user id in token");
    }
}
