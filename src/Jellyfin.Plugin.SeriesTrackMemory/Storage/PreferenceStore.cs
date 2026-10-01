using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.SeriesTrackMemory.Matching;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesTrackMemory.Storage;

/// <summary>
/// What the user wants for subtitles in a series.
/// </summary>
public enum SubtitleChoice
{
    /// <summary>Nothing learned, keep Jellyfin defaults.</summary>
    Unset,

    /// <summary>Subtitles turned off.</summary>
    Off,

    /// <summary>A specific subtitle track.</summary>
    Track
}

/// <summary>
/// Learned track preference of one user for one series.
/// </summary>
public sealed class SeriesPreference
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the series id.</summary>
    public Guid SeriesId { get; set; }

    /// <summary>Gets or sets the series name, for display.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the learned audio track, or null to keep defaults.</summary>
    public TrackDescriptor? Audio { get; set; }

    /// <summary>Gets or sets the subtitle choice.</summary>
    public SubtitleChoice SubtitleChoice { get; set; }

    /// <summary>Gets or sets the learned subtitle track when <see cref="SubtitleChoice"/> is Track.</summary>
    public TrackDescriptor? Subtitle { get; set; }

    /// <summary>Gets or sets the episode the preference was learned from.</summary>
    public Guid SourceEpisodeId { get; set; }

    /// <summary>Gets or sets when the preference was learned.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Whether both preferences describe the same selection.
    /// </summary>
    /// <param name="other">Other preference.</param>
    /// <returns>True when equal.</returns>
    public bool SameSelectionAs(SeriesPreference? other)
        => other is not null
           && Equals(Audio, other.Audio)
           && SubtitleChoice == other.SubtitleChoice
           && Equals(Subtitle, other.Subtitle);
}

/// <summary>
/// Thread-safe JSON persistence of learned preferences.
/// </summary>
public sealed class PreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<PreferenceStore> _logger;
    private Dictionary<(Guid UserId, Guid SeriesId), SeriesPreference>? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferenceStore"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public PreferenceStore(IApplicationPaths paths, ILogger<PreferenceStore> logger)
        : this(Path.Combine(paths.PluginConfigurationsPath, "SeriesTrackMemory", "preferences.json"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferenceStore"/> class with an explicit file.
    /// </summary>
    /// <param name="path">JSON file path.</param>
    /// <param name="logger">Logger.</param>
    public PreferenceStore(string path, ILogger<PreferenceStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Gets a preference.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="seriesId">Series id.</param>
    /// <returns>The preference or null.</returns>
    public SeriesPreference? Get(Guid userId, Guid seriesId)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault((userId, seriesId));
        }
    }

    /// <summary>Gets all preferences of a series, across users.</summary>
    /// <param name="seriesId">Series id.</param>
    /// <returns>The preferences.</returns>
    public IReadOnlyList<SeriesPreference> GetForSeries(Guid seriesId)
    {
        lock (_lock)
        {
            return Load().Values.Where(p => p.SeriesId == seriesId).ToList();
        }
    }

    /// <summary>Gets all preferences.</summary>
    /// <returns>The preferences.</returns>
    public IReadOnlyList<SeriesPreference> GetAll()
    {
        lock (_lock)
        {
            return Load().Values.OrderBy(p => p.SeriesName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Adds or replaces a preference.</summary>
    /// <param name="preference">Preference.</param>
    public void Save(SeriesPreference preference)
    {
        lock (_lock)
        {
            Load()[(preference.UserId, preference.SeriesId)] = preference;
            Persist();
        }
    }

    /// <summary>Removes a preference.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="seriesId">Series id.</param>
    /// <returns>True when something was removed.</returns>
    public bool Remove(Guid userId, Guid seriesId)
    {
        lock (_lock)
        {
            var removed = Load().Remove((userId, seriesId));
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    private Dictionary<(Guid UserId, Guid SeriesId), SeriesPreference> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _cache = new Dictionary<(Guid, Guid), SeriesPreference>();
        if (!File.Exists(_path))
        {
            return _cache;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<SeriesPreference>>(File.ReadAllText(_path), JsonOptions) ?? new();
            foreach (var item in items)
            {
                _cache[(item.UserId, item.SeriesId)] = item;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Could not read {Path}; starting with no learned preferences", _path);
        }

        return _cache;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache!.Values.ToList(), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not write {Path}", _path);
        }
    }
}
