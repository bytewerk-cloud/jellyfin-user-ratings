using System;
using System.Linq;
using Jellyfin.Plugin.Ducks.Configuration;
using Jellyfin.Plugin.Ducks.Injection;
using Jellyfin.Plugin.Ducks.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Ducks.Tests;

public class PluginTests
{
    [Fact]
    public void Plugin_identity_and_config_page()
    {
        var paths = new Mock<IApplicationPaths>();
        foreach (var prop in typeof(IApplicationPaths).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            var parameter = System.Linq.Expressions.Expression.Parameter(typeof(IApplicationPaths));
            var getter = System.Linq.Expressions.Expression.Lambda<Func<IApplicationPaths, string>>(System.Linq.Expressions.Expression.Property(parameter, prop), parameter);
            paths.SetupGet(getter).Returns(System.IO.Path.GetTempPath());
        }

        var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());

        Assert.Same(plugin, Plugin.Instance);
        Assert.Equal("Ducks", plugin.Name);
        Assert.Equal(Guid.Parse("6f3c2b1e-8d4a-4c7e-9b2f-d0c5a1e4f7b3"), plugin.Id);
        Assert.False(string.IsNullOrEmpty(plugin.Description));
        var page = Assert.Single(plugin.GetPages());
        Assert.Equal("Jellyfin.Plugin.Ducks.Configuration.configPage.html", page.EmbeddedResourcePath);
        Assert.NotNull(typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath));
    }

    [Fact]
    public void Default_configuration_limits()
    {
        var config = new PluginConfiguration();
        Assert.Equal(100, config.MaxTitleLength);
        Assert.Equal(2000, config.MaxBodyLength);
        Assert.Equal(7, config.BackupsToKeep);
    }

    [Fact]
    public void Registrator_adds_service_and_injection()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        Assert.Contains(services, d => d.ServiceType == typeof(DucksService) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WebInjectionService));
    }
}
