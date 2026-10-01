using Jellyfin.Plugin.SeriesTrackMemory.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeriesTrackMemory;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PreferenceStore>();
        serviceCollection.AddSingleton<SeriesTrackEngine>();
        serviceCollection.AddHostedService<SeriesTrackMemoryService>();
    }
}
