using System;
using Newtonsoft.Json.Linq;

// Stands in for the real File Transformation plugin's entry point (same full type name).
namespace Jellyfin.Plugin.FileTransformation;

public static class PluginInterface
{
    public static JObject? Received { get; set; }

    public static bool Throw { get; set; }

    public static void RegisterTransformation(JObject payload)
    {
        if (Throw)
        {
            throw new InvalidOperationException("boom");
        }

        Received = payload;
    }
}
