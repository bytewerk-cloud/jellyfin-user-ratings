using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Ducks.Injection;

/// <summary>
/// Registers an index.html transformation with the File Transformation plugin so jellyfin-web loads ducks.js.
/// The web files on disk are never modified, so server and web updates don't undo it.
/// </summary>
public sealed class WebInjectionService : IHostedService
{
    public const string FileTransformationId = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90";

    private static readonly Guid TransformationId = Guid.Parse("b7d0f6c2-3e51-4a8f-9c6d-2f1e8a4b5c7d");

    private readonly ILogger<WebInjectionService> _logger;

    public WebInjectionService(ILogger<WebInjectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the transformation was registered on this server start.</summary>
    public static bool IsRegistered { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRegistered = Register(FindFileTransformationAssembly());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static Assembly? FindFileTransformationAssembly()
    {
        return AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Registers the index.html callback with File Transformation's PluginInterface found in <paramref name="assembly"/>.
    /// It lives in another load context, so it can only be called through reflection with a Newtonsoft JObject.
    /// </summary>
    internal bool Register(Assembly? assembly)
    {
        try
        {
            var register = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
            if (register is null)
            {
                _logger.LogWarning("Ducks: the File Transformation plugin is not installed. Ratings work through the API, but the web UI will not show them. Install File Transformation from https://www.iamparadox.dev/jellyfin/plugins/manifest.json");
                return false;
            }

            var payload = new JObject
            {
                { "id", TransformationId.ToString() },
                { "fileNamePattern", "index.html$" },
                { "callbackAssembly", typeof(IndexHtmlTransformer).Assembly.FullName },
                { "callbackClass", typeof(IndexHtmlTransformer).FullName },
                { "callbackMethod", nameof(IndexHtmlTransformer.Transform) }
            };
            register.Invoke(null, [payload]);
            _logger.LogInformation("Ducks: web UI script registered with File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ducks: failed to register with File Transformation");
            return false;
        }
    }

}

/// <summary>Payload File Transformation passes to the callback.</summary>
public sealed class TransformPayload
{
    public string? Contents { get; set; }
}

/// <summary>
/// Called by File Transformation (via reflection) every time index.html is served.
/// </summary>
public static class IndexHtmlTransformer
{
    private const string Marker = "<!-- ducks -->";

    public static string Transform(TransformPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var v = Uri.EscapeDataString(typeof(IndexHtmlTransformer).Assembly.GetName().Version?.ToString() ?? "0");

        // Relative to /web/, so it also works when Jellyfin runs under a base URL.
        var tags = Marker
            + $"<link rel=\"stylesheet\" href=\"../Ducks/ducks.css?v={v}\">"
            + $"<script defer src=\"../Ducks/ducks.js?v={v}\"></script>";
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + tags : html.Insert(at, tags);
    }
}
