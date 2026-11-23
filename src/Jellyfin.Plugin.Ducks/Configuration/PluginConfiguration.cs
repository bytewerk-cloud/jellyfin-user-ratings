using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ducks.Configuration;

/// <summary>
/// Admin-editable settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public int MaxTitleLength { get; set; } = 100;

    public int MaxBodyLength { get; set; } = 2000;

    public int BackupsToKeep { get; set; } = 7;
}
