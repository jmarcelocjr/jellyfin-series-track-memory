using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeriesTrackMemory.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SeriesTrackMemory.Tests;

public sealed class SeriesTrackEngineTests : IDisposable
{
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly User _user = new("marcelo", "provider", "reset");
    private readonly Dictionary<Guid, Episode> _episodes = new();
    private readonly Dictionary<Guid, List<MediaStream>> _streams = new();
    private readonly Dictionary<Guid, UserItemData> _userData = new();
    private readonly List<(Guid ItemId, UserDataSaveReason Reason)> _saves = new();
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "stm-" + Guid.NewGuid() + ".json");
    private readonly SeriesTrackEngine _engine;
    private readonly PreferenceStore _store;

    public SeriesTrackEngineTests()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _episodes.GetValueOrDefault(id));
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => _episodes.Values.Cast<BaseItem>().ToList());

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(_user.Id)).Returns(_user);

        var userData = new Mock<IUserDataManager>();
        userData.Setup(u => u.GetUserData(_user, It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem item) => _userData.TryGetValue(item.Id, out var d) ? d : _userData[item.Id] = new UserItemData { Key = item.Id.ToString("N") });
        userData.Setup(u => u.SaveUserData(_user, It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BaseItem item, UserItemData data, UserDataSaveReason reason, CancellationToken _) =>
            {
                _userData[item.Id] = data;
                _saves.Add((item.Id, reason));
            });

        var media = new Mock<IMediaSourceManager>();
        media.Setup(m => m.GetMediaStreams(It.IsAny<Guid>())).Returns((Guid id) => _streams.GetValueOrDefault(id) ?? new List<MediaStream>());

        _store = new PreferenceStore(_storePath, NullLogger<PreferenceStore>.Instance);
        _engine = new SeriesTrackEngine(library.Object, users.Object, userData.Object, media.Object, _store, NullLogger<SeriesTrackEngine>.Instance);
    }

    public void Dispose() => File.Delete(_storePath);

    private Episode AddEpisode(params MediaStream[] streams)
    {
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = _seriesId, SeriesName = "Frieren", Name = "Ep" + (_episodes.Count + 1) };
        _episodes[episode.Id] = episode;
        _streams[episode.Id] = streams.ToList();
        return episode;
    }

    private static MediaStream A(int index, string lang) => new() { Index = index, Type = MediaStreamType.Audio, Language = lang };

    private static MediaStream S(int index, string lang, bool forced = false) => new() { Index = index, Type = MediaStreamType.Subtitle, Language = lang, IsForced = forced };

    [Fact]
    public void Learn_PropagatesEquivalentIndicesToOtherEpisodes()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"), S(3, "eng"), S(4, "por"));
        var ep2 = AddEpisode(A(1, "jpn"), A(2, "eng"), S(3, "por"), S(4, "eng"));
        var ep3 = AddEpisode(A(1, "jpn"), S(2, "por"));

        var updated = _engine.Learn(_user.Id, ep1.Id, 2, 4);

        Assert.Equal(2, updated);
        Assert.Equal(1, _userData[ep2.Id].AudioStreamIndex);
        Assert.Equal(3, _userData[ep2.Id].SubtitleStreamIndex);
        Assert.Equal(1, _userData[ep3.Id].AudioStreamIndex);
        Assert.Equal(2, _userData[ep3.Id].SubtitleStreamIndex);
        Assert.All(_saves, s => Assert.Equal(SeriesTrackEngine.OwnSaveReason, s.Reason));
        Assert.False(SeriesTrackEngine.IsLearnableReason(SeriesTrackEngine.OwnSaveReason));
    }

    [Fact]
    public void Learn_SubtitleOffIsPropagatedAsMinusOne()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "por"), S(3, "por"));
        var ep2 = AddEpisode(A(1, "eng"), A(2, "por"), S(3, "por"));

        _engine.Learn(_user.Id, ep1.Id, 2, -1);

        Assert.Equal(2, _userData[ep2.Id].AudioStreamIndex);
        Assert.Equal(-1, _userData[ep2.Id].SubtitleStreamIndex);
    }

    [Fact]
    public void Learn_SameSelectionTwiceDoesNothing()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        AddEpisode(A(1, "eng"), A(2, "jpn"));

        Assert.Equal(1, _engine.Learn(_user.Id, ep1.Id, 2, null));
        Assert.Equal(-1, _engine.Learn(_user.Id, ep1.Id, 2, null));
    }

    [Fact]
    public void Learn_FallbackTrackInEpisodeWithoutLearnedLanguageDoesNotOverwrite()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        var epWithoutJapanese = AddEpisode(A(1, "eng"));
        _engine.Learn(_user.Id, ep1.Id, 2, null);

        // User watches the English-only episode: it plays English because there is no choice.
        _engine.Learn(_user.Id, epWithoutJapanese.Id, 1, null);

        Assert.Equal("jpn", _store.Get(_user.Id, _seriesId)!.Audio!.Language);
    }

    [Fact]
    public void Learn_RespectsRememberSelectionsSetting()
    {
        _user.RememberAudioSelections = false;
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"), S(3, "por"));
        var ep2 = AddEpisode(A(1, "eng"), A(2, "jpn"), S(3, "por"));

        _engine.Learn(_user.Id, ep1.Id, 2, 3);

        Assert.Null(_userData[ep2.Id].AudioStreamIndex);
        Assert.Equal(3, _userData[ep2.Id].SubtitleStreamIndex);
    }

    [Fact]
    public void ApplyToEpisode_FillsOnlyEmptySelections()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"), S(3, "por"));
        _engine.Learn(_user.Id, ep1.Id, 2, 3);

        var fresh = AddEpisode(A(1, "jpn"), A(2, "eng"), S(3, "por"));
        var manual = AddEpisode(A(1, "jpn"), A(2, "eng"), S(3, "por"));
        _userData[manual.Id] = new UserItemData { Key = "k", AudioStreamIndex = 2, SubtitleStreamIndex = 3 };

        _engine.ApplyToEpisode(fresh);
        _engine.ApplyToEpisode(manual);

        Assert.Equal(1, _userData[fresh.Id].AudioStreamIndex);
        Assert.Equal(2, _userData[manual.Id].AudioStreamIndex);
    }

    [Fact]
    public void ApplyToEpisode_SkipsEpisodesNotProbedYet()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        _engine.Learn(_user.Id, ep1.Id, 2, null);

        var unprobed = AddEpisode();

        Assert.Equal(0, _engine.ApplyToEpisode(unprobed));
    }

    [Fact]
    public void Forget_RemovesPreferenceAndClearsSelections()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        var ep2 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        _engine.Learn(_user.Id, ep1.Id, 2, null);

        Assert.True(_engine.Forget(_user.Id, _seriesId));

        Assert.Null(_store.Get(_user.Id, _seriesId));
        Assert.Null(_userData[ep2.Id].AudioStreamIndex);
        Assert.False(_engine.Forget(_user.Id, _seriesId));
    }

    [Fact]
    public void Store_PersistsAcrossInstances()
    {
        var ep1 = AddEpisode(A(1, "eng"), A(2, "jpn"));
        _engine.Learn(_user.Id, ep1.Id, 2, -1);

        var reloaded = new PreferenceStore(_storePath, NullLogger<PreferenceStore>.Instance).Get(_user.Id, _seriesId);

        Assert.NotNull(reloaded);
        Assert.Equal("jpn", reloaded!.Audio!.Language);
        Assert.Equal(SubtitleChoice.Off, reloaded.SubtitleChoice);
    }
}
