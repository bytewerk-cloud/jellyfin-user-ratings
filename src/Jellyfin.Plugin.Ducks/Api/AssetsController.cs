using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ducks.Api;

/// <summary>
/// Serves the embedded web client files. Anonymous because the login page loads index.html too.
/// </summary>
[ApiController]
[Route("Ducks")]
[AllowAnonymous]
public class AssetsController : ControllerBase
{
    [HttpGet("ducks.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetScript() => Embedded("ducks.js", "application/javascript; charset=utf-8");

    [HttpGet("ducks.css")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStyles() => Embedded("ducks.css", "text/css; charset=utf-8");

    private ActionResult Embedded(string name, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Ducks.Web." + name);
        if (stream is null)
        {
            return NotFound();
        }

        // URLs carry ?v=<plugin version>, so a long cache is safe.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, contentType);
    }
}
