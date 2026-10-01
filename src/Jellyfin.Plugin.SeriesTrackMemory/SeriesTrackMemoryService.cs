using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeriesTrackMemory.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesTrackMemory;

/// <summary>
/// Listens to Jellyfin events and feeds the engine from a single background worker,
/// so request threads are never blocked.
/// </summary>
public sealed class SeriesTrackMemoryService : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly SeriesTrackEngine _engine;
    private readonly ILogger<SeriesTrackMemoryService> _logger;
    private readonly Channel<Func<SeriesTrackEngine, object>> _queue =
        Channel.CreateUnbounded<Func<SeriesTrackEngine, object>>(new UnboundedChannelOptions { SingleReader = true });

    // Last selection seen per (user, episode): progress events arrive every few seconds,
    // only a change of selection is worth processing.
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), (int? Audio, int? Subtitle)> _lastSeen = new();

    private CancellationTokenSource? _stopping;
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesTrackMemoryService"/> class.
    /// </summary>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="engine">Engine.</param>
    /// <param name="logger">Logger.</param>
    public SeriesTrackMemoryService(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        SeriesTrackEngine engine,
        ILogger<SeriesTrackMemoryService> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _engine = engine;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _queue.Writer.TryComplete();
        _stopping?.Cancel();
        if (_worker is not null)
        {
            await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (!Config.EnableLearning
            || !SeriesTrackEngine.IsLearnableReason(e.SaveReason)
            || e.Item is not Episode
            || e.UserData is null)
        {
            return;
        }

        var audio = e.UserData.AudioStreamIndex;
        var subtitle = e.UserData.SubtitleStreamIndex;
        if (audio is null && subtitle is null)
        {
            return;
        }

        var key = (e.UserId, e.Item.Id);
        var selection = (audio, subtitle);
        if (_lastSeen.TryGetValue(key, out var previous) && previous == selection)
        {
            return;
        }

        _lastSeen[key] = selection;
        if (_lastSeen.Count > 5000)
        {
            _lastSeen.Clear();
        }

        var itemId = e.Item.Id;
        var userId = e.UserId;
        _queue.Writer.TryWrite(engine => engine.Learn(userId, itemId, audio, subtitle));
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (!Config.ApplyToNewEpisodes || e.Item is not Episode episode)
        {
            return;
        }

        BaseItem item = episode;
        _queue.Writer.TryWrite(engine => engine.ApplyToEpisode(item));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    work(_engine);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Series Track Memory failed to process an event");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
