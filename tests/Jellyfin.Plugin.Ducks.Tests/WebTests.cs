using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ducks.Api;
using Jellyfin.Plugin.Ducks.Injection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public class WebTests
{
    [Fact]
    public void Transformer_injects_css_and_script_before_body_end()
    {
        var html = IndexHtmlTransformer.Transform(new TransformPayload { Contents = "<html><body><div></div></body></html>" });
        Assert.Matches("<link rel=\"stylesheet\" href=\"../Ducks/ducks.css\\?v=[0-9.]+\"><script defer src=\"../Ducks/ducks.js\\?v=[0-9.]+\"></script></body></html>$", html);
    }

    [Fact]
    public void Transformer_is_idempotent()
    {
        var once = IndexHtmlTransformer.Transform(new TransformPayload { Contents = "<body></body>" });
        Assert.Equal(once, IndexHtmlTransformer.Transform(new TransformPayload { Contents = once }));
    }

    [Fact]
    public void Transformer_appends_when_no_body_tag_and_handles_null()
    {
        Assert.StartsWith("<p>x</p><!-- ducks -->", IndexHtmlTransformer.Transform(new TransformPayload { Contents = "<p>x</p>" }), StringComparison.Ordinal);
        Assert.StartsWith("<!-- ducks -->", IndexHtmlTransformer.Transform(new TransformPayload()), StringComparison.Ordinal);
    }

    [Fact]
    public void Register_passes_callback_details_to_file_transformation()
    {
        Jellyfin.Plugin.FileTransformation.PluginInterface.Received = null;
        Jellyfin.Plugin.FileTransformation.PluginInterface.Throw = false;
        var service = new WebInjectionService(NullLogger<WebInjectionService>.Instance);

        Assert.True(service.Register(typeof(WebTests).Assembly));

        var payload = Jellyfin.Plugin.FileTransformation.PluginInterface.Received!;
        Assert.Equal("index.html$", (string?)payload["fileNamePattern"]);
        Assert.Equal(typeof(IndexHtmlTransformer).FullName, (string?)payload["callbackClass"]);
        Assert.Equal(nameof(IndexHtmlTransformer.Transform), (string?)payload["callbackMethod"]);
        Assert.Equal(typeof(IndexHtmlTransformer).Assembly.FullName, (string?)payload["callbackAssembly"]);
        Assert.True(Guid.TryParse((string?)payload["id"], out _));
    }

    [Fact]
    public void Register_reports_missing_or_failing_file_transformation()
    {
        var service = new WebInjectionService(NullLogger<WebInjectionService>.Instance);
        Assert.False(service.Register(null));
        Assert.False(service.Register(typeof(object).Assembly));

        Jellyfin.Plugin.FileTransformation.PluginInterface.Throw = true;
        try
        {
            Assert.False(service.Register(typeof(WebTests).Assembly));
        }
        finally
        {
            Jellyfin.Plugin.FileTransformation.PluginInterface.Throw = false;
        }
    }

    [Fact]
    public async Task Hosted_service_start_and_stop_complete()
    {
        var service = new WebInjectionService(NullLogger<WebInjectionService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(WebInjectionService.FindFileTransformationAssembly() is not null, WebInjectionService.IsRegistered);
    }

    [Theory]
    [InlineData("js", "application/javascript; charset=utf-8")]
    [InlineData("css", "text/css; charset=utf-8")]
    public void Assets_are_served_from_embedded_resources(string kind, string contentType)
    {
        var controller = new AssetsController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<FileStreamResult>(kind == "js" ? controller.GetScript() : controller.GetStyles());

        Assert.Equal(contentType, result.ContentType);
        Assert.True(result.FileStream.Length > 0);
        Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Missing_asset_returns_404()
    {
        var controller = new AssetsController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var method = typeof(AssetsController).GetMethod("Embedded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.IsType<NotFoundResult>(method.Invoke(controller, ["nope.js", "text/plain"]));
    }
}
