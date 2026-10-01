using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SeriesTrackMemory.Matching;

/// <summary>
/// Kind of track.
/// </summary>
public enum TrackKind
{
    /// <summary>Audio track.</summary>
    Audio,

    /// <summary>Subtitle track.</summary>
    Subtitle
}

/// <summary>
/// A stream of an episode, detached from Jellyfin types so the matching logic is pure.
/// </summary>
/// <param name="Index">Stream index inside the media source.</param>
/// <param name="Kind">Audio or subtitle.</param>
/// <param name="Language">Language as reported by Jellyfin (usually ISO 639-2).</param>
/// <param name="Title">Stream title.</param>
/// <param name="IsForced">Forced flag.</param>
/// <param name="IsHearingImpaired">SDH / hearing impaired flag.</param>
/// <param name="IsDefault">Default flag in the container.</param>
/// <param name="IsExternal">Whether the stream is an external file.</param>
public sealed record TrackCandidate(
    int Index,
    TrackKind Kind,
    string? Language,
    string? Title,
    bool IsForced,
    bool IsHearingImpaired,
    bool IsDefault,
    bool IsExternal);

/// <summary>
/// Description of a track the user picked, used to find the equivalent track in other episodes.
/// </summary>
public sealed class TrackDescriptor : IEquatable<TrackDescriptor>
{
    /// <summary>Gets or sets the normalized language (ISO 639-2/B), or null when unknown.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the raw language string reported by the file.</summary>
    public string? RawLanguage { get; set; }

    /// <summary>Gets or sets the stream title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is forced.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is SDH.</summary>
    public bool IsHearingImpaired { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is external.</summary>
    public bool IsExternal { get; set; }

    /// <summary>Gets or sets the position of the stream among streams of the same kind.</summary>
    public int Ordinal { get; set; }

    /// <inheritdoc />
    public bool Equals(TrackDescriptor? other)
        => other is not null
           && string.Equals(Language, other.Language, StringComparison.Ordinal)
           && string.Equals(RawLanguage, other.RawLanguage, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Title, other.Title, StringComparison.Ordinal)
           && IsForced == other.IsForced
           && IsHearingImpaired == other.IsHearingImpaired
           && IsExternal == other.IsExternal
           && Ordinal == other.Ordinal;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as TrackDescriptor);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Language, Title, IsForced, IsHearingImpaired, IsExternal, Ordinal);

    /// <summary>Short human readable label.</summary>
    /// <returns>The label.</returns>
    public override string ToString()
    {
        var label = Language ?? RawLanguage ?? "und";
        if (!string.IsNullOrWhiteSpace(Title))
        {
            label += " \"" + Title + "\"";
        }

        if (IsForced)
        {
            label += " (forced)";
        }

        if (IsHearingImpaired)
        {
            label += " (SDH)";
        }

        return label;
    }
}

/// <summary>
/// Pure logic that describes a selected track and finds its equivalent in another episode.
/// </summary>
public static class TrackMatcher
{
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // ISO 639-1 to ISO 639-2/B.
        ["pt"] = "por", ["en"] = "eng", ["ja"] = "jpn", ["es"] = "spa", ["fr"] = "fre",
        ["de"] = "ger", ["it"] = "ita", ["ko"] = "kor", ["zh"] = "chi", ["ru"] = "rus",
        ["nl"] = "dut", ["pl"] = "pol", ["sv"] = "swe", ["tr"] = "tur", ["ar"] = "ara",
        ["hi"] = "hin", ["cs"] = "cze", ["el"] = "gre", ["he"] = "heb", ["da"] = "dan",
        ["fi"] = "fin", ["no"] = "nor", ["nb"] = "nor", ["hu"] = "hun", ["ro"] = "rum",
        ["th"] = "tha", ["uk"] = "ukr", ["vi"] = "vie", ["id"] = "ind", ["ms"] = "may",

        // ISO 639-2/T to ISO 639-2/B.
        ["fra"] = "fre", ["deu"] = "ger", ["zho"] = "chi", ["nld"] = "dut", ["ces"] = "cze",
        ["ell"] = "gre", ["ron"] = "rum", ["msa"] = "may",

        // Non-standard codes seen in the wild.
        ["pob"] = "por", ["ptbr"] = "por", ["jap"] = "jpn",

        // Full names.
        ["portuguese"] = "por", ["english"] = "eng", ["japanese"] = "jpn", ["spanish"] = "spa",
        ["french"] = "fre", ["german"] = "ger", ["italian"] = "ita", ["korean"] = "kor",
        ["chinese"] = "chi"
    };

    private static readonly HashSet<string> UnknownLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "und", "unk", "unknown", "mis", "mul", "zxx", "xx", "none"
    };

    private static readonly char[] TokenSeparators =
    {
        ' ', '-', '_', '.', ',', '(', ')', '[', ']', '/', '|', ':', ';', '"', '\''
    };

    /// <summary>
    /// Normalizes a language string to ISO 639-2/B, or null when unknown.
    /// </summary>
    /// <param name="language">Raw language.</param>
    /// <returns>Normalized language or null.</returns>
    public static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var value = language.Trim();
        if (UnknownLanguages.Contains(value))
        {
            return null;
        }

        if (LanguageAliases.TryGetValue(value.Replace("-", string.Empty, StringComparison.Ordinal), out var direct))
        {
            return direct;
        }

        // "pt-BR", "en_US" -> base language.
        var baseLanguage = value.Split('-', '_')[0];
        if (UnknownLanguages.Contains(baseLanguage))
        {
            return null;
        }

        if (LanguageAliases.TryGetValue(baseLanguage, out var mapped))
        {
            return mapped;
        }

        return baseLanguage.ToLowerInvariant();
    }

    /// <summary>
    /// Builds a descriptor for the stream with the given index.
    /// </summary>
    /// <param name="streams">All streams of the episode.</param>
    /// <param name="kind">Kind of track.</param>
    /// <param name="index">Selected stream index.</param>
    /// <returns>The descriptor, or null when the index does not point to a stream of that kind.</returns>
    public static TrackDescriptor? Describe(IReadOnlyList<TrackCandidate> streams, TrackKind kind, int index)
    {
        var ofKind = streams.Where(s => s.Kind == kind).OrderBy(s => s.Index).ToList();
        var position = ofKind.FindIndex(s => s.Index == index);
        if (position < 0)
        {
            return null;
        }

        var stream = ofKind[position];
        return new TrackDescriptor
        {
            Language = NormalizeLanguage(stream.Language),
            RawLanguage = stream.Language,
            Title = string.IsNullOrWhiteSpace(stream.Title) ? null : stream.Title.Trim(),
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            IsExternal = stream.IsExternal,
            Ordinal = position
        };
    }

    /// <summary>
    /// Finds the stream in <paramref name="streams"/> equivalent to <paramref name="wanted"/>.
    /// </summary>
    /// <param name="streams">All streams of the target episode.</param>
    /// <param name="kind">Kind of track.</param>
    /// <param name="wanted">Descriptor learned from another episode.</param>
    /// <returns>The index of the best match, or null when nothing is a safe match.</returns>
    public static int? FindMatch(IReadOnlyList<TrackCandidate> streams, TrackKind kind, TrackDescriptor wanted)
    {
        var ofKind = streams.Where(s => s.Kind == kind).OrderBy(s => s.Index).ToList();
        if (ofKind.Count == 0)
        {
            return null;
        }

        var wantedTokens = Tokenize(wanted.Title);
        var wantedRegion = RegionHint(wanted.RawLanguage, wanted.Title);

        IEnumerable<(TrackCandidate Stream, int Ordinal)> candidates = ofKind.Select((s, i) => (s, i));

        if (wanted.Language is not null)
        {
            // Language is mandatory when known.
            candidates = candidates.Where(c => NormalizeLanguage(c.Stream.Language) == wanted.Language);
        }
        else
        {
            // Unknown language: only consider streams that are also untagged.
            candidates = candidates.Where(c => NormalizeLanguage(c.Stream.Language) is null);
        }

        var scored = candidates
            .Select(c =>
            {
                var score = 0d;
                if (c.Stream.IsForced == wanted.IsForced)
                {
                    score += 8;
                }

                if (c.Stream.IsHearingImpaired == wanted.IsHearingImpaired)
                {
                    score += 4;
                }

                var candidateRegion = RegionHint(c.Stream.Language, c.Stream.Title);
                if (wantedRegion is not null && candidateRegion is not null)
                {
                    score += wantedRegion == candidateRegion ? 6 : -6;
                }

                score += 4 * Similarity(wantedTokens, Tokenize(c.Stream.Title));

                if (string.Equals(c.Stream.Language, wanted.RawLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                }

                if (c.Stream.IsExternal == wanted.IsExternal)
                {
                    score += 1;
                }

                if (c.Ordinal == wanted.Ordinal)
                {
                    score += 0.5;
                }

                if (c.Stream.IsDefault)
                {
                    score += 0.25;
                }

                return (c.Stream, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Stream.Index)
            .ToList();

        if (scored.Count > 0)
        {
            return scored[0].Stream.Index;
        }

        // Nothing tagged the same way. For untagged picks, fall back to the same position
        // (batch releases usually have identical layouts).
        if (wanted.Language is null && wanted.Ordinal < ofKind.Count)
        {
            return ofKind[wanted.Ordinal].Index;
        }

        return null;
    }

    private static string? RegionHint(string? language, string? title)
    {
        var text = ((language ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();
        foreach (var token in Tokenize(text))
        {
            switch (token)
            {
                case "br":
                case "bra":
                case "brazil":
                case "brazilian":
                case "brasil":
                case "brasileiro":
                case "pob":
                case "ptbr":
                    return "br";
                case "pt":
                case "portugal":
                case "european":
                case "europeu":
                    // "pt" alone is ambiguous; only count it as Portugal when paired with "pt-PT".
                    if (token == "pt" && !text.Contains("pt-pt", StringComparison.Ordinal))
                    {
                        break;
                    }

                    return "pt";
                case "latino":
                case "latam":
                case "lat":
                    return "latam";
                case "castellano":
                case "castilian":
                case "spain":
                    return "es";
            }
        }

        return null;
    }

    private static HashSet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return text.ToLowerInvariant()
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return (double)intersection / union;
    }
}
