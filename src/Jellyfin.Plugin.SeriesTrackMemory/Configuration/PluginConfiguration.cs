using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeriesTrackMemory.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether track choices are learned during playback.
    /// </summary>
    public bool EnableLearning { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether learned choices are applied to newly added episodes.
    /// </summary>
    public bool ApplyToNewEpisodes { get; set; } = true;
}
