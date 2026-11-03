using System.Globalization;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.HueSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

[assembly: Guid("7c4e1a2b-9f63-4d18-b5a0-2e8c6f91d4ab")]

namespace Jellyfin.Plugin.HueSync;

/// <summary>
/// Raises Hue entertainment lights while Jellyfin is paused, then resumes Hue Sync.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Hue Sync Pause";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c4e1a2b-9f63-4d18-b5a0-2e8c6f91d4ab");

    /// <inheritdoc />
    public override string Description => "While Hue Sync is on, pause raises the entertainment lights and play resumes sync.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
