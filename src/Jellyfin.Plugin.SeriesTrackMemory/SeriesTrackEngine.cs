using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeriesTrackMemory.Matching;
using Jellyfin.Plugin.SeriesTrackMemory.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesTrackMemory;

/// <summary>
/// Learns the audio/subtitle choice of a user for a series and writes the equivalent
/// selection into the user data of every episode, where Jellyfin's own
/// "remember selections" logic picks it up on the next playback.
/// </summary>
public class SeriesTrackEngine
{
    /// <summary>
    /// Save reason used for the plugin's own writes. It is not a playback reason,
    /// so these writes never trigger learning again.
    /// </summary>
    public const UserDataSaveReason OwnSaveReason = UserDataSaveReason.UpdateUserData;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly PreferenceStore _store;
    private readonly ILogger<SeriesTrackEngine> _logger;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesTrackEngine"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="store">Preference store.</param>
    /// <param name="logger">Logger.</param>
    public SeriesTrackEngine(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IMediaSourceManager mediaSourceManager,
        PreferenceStore store,
        ILogger<SeriesTrackEngine> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _mediaSourceManager = mediaSourceManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether a save reason comes from actual playback and should be learned from.
    /// PlaybackStart is ignored on purpose: it only reflects the initial defaults.
    /// </summary>
    /// <param name="reason">Save reason.</param>
    /// <returns>True when the save should be learned from.</returns>
    public static bool IsLearnableReason(UserDataSaveReason reason)
        => reason is UserDataSaveReason.PlaybackProgress or UserDataSaveReason.PlaybackFinished;

    /// <summary>
    /// Learns from the tracks a user is playing in an episode and propagates to the series.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="audioStreamIndex">Selected audio stream index.</param>
    /// <param name="subtitleStreamIndex">Selected subtitle stream index, -1 for off.</param>
    /// <returns>Number of episodes updated, or -1 when nothing new was learned.</returns>
    public int Learn(Guid userId, Guid episodeId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        if (_libraryManager.GetItemById(episodeId) is not Episode episode || episode.SeriesId.Equals(Guid.Empty))
        {
            return -1;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return -1;
        }

        var streams = GetCandidates(episode);
        var learned = new SeriesPreference
        {
            UserId = userId,
            SeriesId = episode.SeriesId,
            SeriesName = episode.SeriesName,
            SourceEpisodeId = episode.Id,
            UpdatedUtc = DateTime.UtcNow
        };

        if (audioStreamIndex.HasValue && user.RememberAudioSelections)
        {
            learned.Audio = TrackMatcher.Describe(streams, TrackKind.Audio, audioStreamIndex.Value);
        }

        if (subtitleStreamIndex.HasValue && user.RememberSubtitleSelections)
        {
            if (subtitleStreamIndex.Value < 0)
            {
                learned.SubtitleChoice = SubtitleChoice.Off;
            }
            else
            {
                learned.Subtitle = TrackMatcher.Describe(streams, TrackKind.Subtitle, subtitleStreamIndex.Value);
                learned.SubtitleChoice = learned.Subtitle is null ? SubtitleChoice.Unset : SubtitleChoice.Track;
            }
        }

        lock (_gate)
        {
            var existing = _store.Get(userId, episode.SeriesId);

            // Keep whatever part this event did not carry. Also keep the learned part when this
            // episode does not even have the learned track: the user could not have picked it here,
            // so the current track is a fallback, not a new choice.
            if (existing?.Audio is not null
                && (learned.Audio is null || TrackMatcher.FindMatch(streams, TrackKind.Audio, existing.Audio) is null))
            {
                learned.Audio = existing.Audio;
            }

            if (existing is not null
                && existing.SubtitleChoice == SubtitleChoice.Track
                && learned.SubtitleChoice == SubtitleChoice.Track
                && TrackMatcher.FindMatch(streams, TrackKind.Subtitle, existing.Subtitle!) is null)
            {
                learned.SubtitleChoice = existing.SubtitleChoice;
                learned.Subtitle = existing.Subtitle;
            }

            if (learned.SubtitleChoice == SubtitleChoice.Unset && existing is not null)
            {
                learned.SubtitleChoice = existing.SubtitleChoice;
                learned.Subtitle = existing.Subtitle;
            }

            if (learned.Audio is null && learned.SubtitleChoice == SubtitleChoice.Unset)
            {
                return -1;
            }

            if (learned.SameSelectionAs(existing))
            {
                return -1;
            }

            _store.Save(learned);
            _logger.LogInformation(
                "Learned tracks for {User} in {Series}: audio {Audio}, subtitle {Subtitle}",
                user.Username,
                episode.SeriesName,
                learned.Audio?.ToString() ?? "default",
                DescribeSubtitle(learned));

            var updated = Propagate(user, learned, overwrite: true, excludeEpisodeId: episode.Id);
            _logger.LogInformation("Propagated {Series} tracks to {Count} episodes for {User}", episode.SeriesName, updated, user.Username);
            return updated;
        }
    }

    /// <summary>
    /// Applies saved preferences to an episode that was added or refreshed.
    /// Only fills selections the user has not made yet.
    /// </summary>
    /// <param name="item">Added or updated item.</param>
    /// <returns>Number of users updated.</returns>
    public int ApplyToEpisode(BaseItem item)
    {
        if (item is not Episode episode || episode.SeriesId.Equals(Guid.Empty) || episode.IsVirtualItem)
        {
            return 0;
        }

        var preferences = _store.GetForSeries(episode.SeriesId);
        if (preferences.Count == 0)
        {
            return 0;
        }

        var streams = GetCandidates(episode);
        if (streams.Count == 0)
        {
            // Not probed yet; ItemUpdated will call again once streams exist.
            return 0;
        }

        var count = 0;
        lock (_gate)
        {
            foreach (var preference in preferences)
            {
                var user = _userManager.GetUserById(preference.UserId);
                if (user is not null && ApplyToOne(user, episode, streams, preference, overwrite: false))
                {
                    count++;
                }
            }
        }

        if (count > 0)
        {
            _logger.LogInformation("Applied learned tracks of {Series} to new episode {Episode} for {Count} users", episode.SeriesName, episode.Name, count);
        }

        return count;
    }

    /// <summary>
    /// Forgets a preference and clears the selections it wrote in the series.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="seriesId">Series id.</param>
    /// <returns>True when a preference existed.</returns>
    public bool Forget(Guid userId, Guid seriesId)
    {
        lock (_gate)
        {
            if (!_store.Remove(userId, seriesId))
            {
                return false;
            }

            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                return true;
            }

            foreach (var episode in GetEpisodes(seriesId))
            {
                var data = _userDataManager.GetUserData(user, episode);
                if (data is null || (data.AudioStreamIndex is null && data.SubtitleStreamIndex is null))
                {
                    continue;
                }

                data.AudioStreamIndex = null;
                data.SubtitleStreamIndex = null;
                _userDataManager.SaveUserData(user, episode, data, OwnSaveReason, CancellationToken.None);
            }

            _logger.LogInformation("Forgot learned tracks of series {SeriesId} for {User}", seriesId, user.Username);
            return true;
        }
    }

    /// <summary>
    /// Describes the subtitle part of a preference.
    /// </summary>
    /// <param name="preference">Preference.</param>
    /// <returns>Human readable label.</returns>
    public static string DescribeSubtitle(SeriesPreference preference) => preference.SubtitleChoice switch
    {
        SubtitleChoice.Off => "off",
        SubtitleChoice.Track => preference.Subtitle?.ToString() ?? "default",
        _ => "default"
    };

    private int Propagate(User user, SeriesPreference preference, bool overwrite, Guid excludeEpisodeId)
    {
        var count = 0;
        foreach (var episode in GetEpisodes(preference.SeriesId))
        {
            if (episode.Id.Equals(excludeEpisodeId))
            {
                continue;
            }

            if (ApplyToOne(user, episode, GetCandidates(episode), preference, overwrite))
            {
                count++;
            }
        }

        return count;
    }

    private bool ApplyToOne(User user, Episode episode, IReadOnlyList<TrackCandidate> streams, SeriesPreference preference, bool overwrite)
    {
        if (streams.Count == 0)
        {
            return false;
        }

        var data = _userDataManager.GetUserData(user, episode);
        if (data is null)
        {
            return false;
        }

        var changed = false;

        if (preference.Audio is not null && user.RememberAudioSelections && (overwrite || data.AudioStreamIndex is null))
        {
            var index = TrackMatcher.FindMatch(streams, TrackKind.Audio, preference.Audio);
            if (index.HasValue && data.AudioStreamIndex != index)
            {
                data.AudioStreamIndex = index;
                changed = true;
            }
        }

        if (preference.SubtitleChoice != SubtitleChoice.Unset && user.RememberSubtitleSelections && (overwrite || data.SubtitleStreamIndex is null))
        {
            int? index = preference.SubtitleChoice == SubtitleChoice.Off
                ? -1
                : TrackMatcher.FindMatch(streams, TrackKind.Subtitle, preference.Subtitle!);
            if (index.HasValue && data.SubtitleStreamIndex != index)
            {
                data.SubtitleStreamIndex = index;
                changed = true;
            }
        }

        if (changed)
        {
            _userDataManager.SaveUserData(user, episode, data, OwnSaveReason, CancellationToken.None);
        }

        return changed;
    }

    private IEnumerable<Episode> GetEpisodes(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { seriesId },
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(e => e.SeriesId.Equals(seriesId));

    private IReadOnlyList<TrackCandidate> GetCandidates(BaseItem item)
        => _mediaSourceManager.GetMediaStreams(item.Id)
            .Where(s => s.Type is MediaStreamType.Audio or MediaStreamType.Subtitle)
            .Select(s => new TrackCandidate(
                s.Index,
                s.Type == MediaStreamType.Audio ? TrackKind.Audio : TrackKind.Subtitle,
                s.Language,
                s.Title,
                s.IsForced,
                s.IsHearingImpaired,
                s.IsDefault,
                s.IsExternal))
            .ToList();
}
