using Jellyfin.Plugin.SeriesTrackMemory.Matching;
using Xunit;

namespace Jellyfin.Plugin.SeriesTrackMemory.Tests;

public class TrackMatcherTests
{
    private static TrackCandidate Audio(int index, string? lang, string? title = null, bool isDefault = false)
        => new(index, TrackKind.Audio, lang, title, false, false, isDefault, false);

    private static TrackCandidate Sub(int index, string? lang, string? title = null, bool forced = false, bool sdh = false, bool external = false)
        => new(index, TrackKind.Subtitle, lang, title, forced, sdh, false, external);

    [Theory]
    [InlineData("pt-BR", "por")]
    [InlineData("pob", "por")]
    [InlineData("por", "por")]
    [InlineData("ja", "jpn")]
    [InlineData("JPN", "jpn")]
    [InlineData("fra", "fre")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeLanguage_MapsCommonCodes(string? input, string? expected)
        => Assert.Equal(expected, TrackMatcher.NormalizeLanguage(input));

    [Fact]
    public void Audio_MatchesSameLanguageAtDifferentIndex()
    {
        // Episode 1: eng=1, jpn=2. Episode 2 has the tracks in another order.
        var ep1 = new[] { Audio(1, "eng"), Audio(2, "jpn") };
        var ep2 = new[] { Audio(1, "jpn"), Audio(2, "eng") };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Audio, 2)!;

        Assert.Equal(1, TrackMatcher.FindMatch(ep2, TrackKind.Audio, wanted));
    }

    [Fact]
    public void Audio_ReturnsNullWhenLanguageMissing()
    {
        var ep1 = new[] { Audio(1, "eng"), Audio(2, "jpn") };
        var ep2 = new[] { Audio(1, "eng") };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Audio, 2)!;

        Assert.Null(TrackMatcher.FindMatch(ep2, TrackKind.Audio, wanted));
    }

    [Fact]
    public void Audio_PrefersSameTitleOverCommentary()
    {
        var ep1 = new[] { Audio(1, "eng", "Stereo"), Audio(2, "eng", "Director Commentary") };
        var ep2 = new[] { Audio(1, "eng", "Director Commentary"), Audio(2, "eng", "Stereo") };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Audio, 1)!;

        Assert.Equal(2, TrackMatcher.FindMatch(ep2, TrackKind.Audio, wanted));
    }

    [Fact]
    public void Subtitle_PrefersFullOverForced()
    {
        var ep1 = new[] { Sub(3, "por", "Forced", forced: true), Sub(4, "por", "Full") };
        var ep2 = new[] { Sub(3, "por", "Full"), Sub(4, "por", "Forced", forced: true) };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Subtitle, 4)!;

        Assert.Equal(3, TrackMatcher.FindMatch(ep2, TrackKind.Subtitle, wanted));
    }

    [Fact]
    public void Subtitle_PrefersBrazilianOverEuropeanPortuguese()
    {
        var ep1 = new[] { Sub(3, "por", "Português (Portugal) pt-PT"), Sub(4, "por", "Português (Brasil)") };
        var ep2 = new[] { Sub(3, "por", "Brazilian"), Sub(4, "por", "European pt-PT") };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Subtitle, 4)!;

        Assert.Equal(3, TrackMatcher.FindMatch(ep2, TrackKind.Subtitle, wanted));
    }

    [Fact]
    public void Subtitle_MatchesExternalFileWithTwoLetterCode()
    {
        var ep1 = new[] { Sub(3, "por") };
        var ep2 = new[] { Sub(2, "eng"), Sub(5, "pt", external: true) };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Subtitle, 3)!;

        Assert.Equal(5, TrackMatcher.FindMatch(ep2, TrackKind.Subtitle, wanted));
    }

    [Fact]
    public void Untagged_FallsBackToSamePosition()
    {
        var ep1 = new[] { Audio(1, null), Audio(2, null) };
        var ep2 = new[] { Audio(1, "und"), Audio(2, "und") };

        var wanted = TrackMatcher.Describe(ep1, TrackKind.Audio, 2)!;

        Assert.Equal(2, TrackMatcher.FindMatch(ep2, TrackKind.Audio, wanted));
    }

    [Fact]
    public void Describe_ReturnsNullForWrongKind()
    {
        var streams = new[] { Audio(1, "eng"), Sub(2, "por") };

        Assert.Null(TrackMatcher.Describe(streams, TrackKind.Subtitle, 1));
    }
}
