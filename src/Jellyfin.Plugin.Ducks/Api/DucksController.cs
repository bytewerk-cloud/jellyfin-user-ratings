using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ducks.Api;

/// <summary>
/// REST endpoints for ratings and reviews.
/// </summary>
[ApiController]
[Route("Ducks")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class DucksController : ControllerBase
{
    /// <summary>
    /// Health check used to verify the plugin is loaded.
    /// </summary>
    [HttpGet("Ping")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Ping()
    {
        return Ok(new { Plugin = "Ducks", Version = Plugin.Instance?.Version.ToString() });
    }
}
