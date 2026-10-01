# Series Track Memory (Jellyfin 12.x)

A Jellyfin plugin that remembers the audio and subtitle you pick for a series, like Netflix does.
Change the tracks in the player once, in any episode. Every other episode of the series then starts with them, on any client.

## How it works

Jellyfin already stores the tracks picked in each episode ("remember selections") and uses them as the default the next time that episode plays.
This plugin extends that to the whole series.

1. While you watch, the server records the selected tracks. The plugin listens to that and describes each track by language, title and forced/SDH flags.
2. It finds the equivalent track in every other episode of the series, even at a different index, and stores it as that episode's remembered selection.
3. When the next episode starts, the server itself hands out those tracks as defaults, so nothing switches after playback begins.
4. Episodes added later get the preference as soon as they are scanned.

Movies and series you never adjusted keep the user's global defaults.
If an episode does not have the learned language, it falls back to the global defaults and the plugin keeps the series preference unchanged.

### Track matching

- The language must match. Codes are normalized, so `pt-BR`, `pt`, `por` and `pob` are all Portuguese.
- Among tracks in the same language, the best score wins: same forced flag, same SDH flag, same region (Brazil vs Portugal, Latin American vs Castilian Spanish), similar title, same embedded/external origin, same position.
- Titles only break ties, so subtitles named differently by different fansubs still match when there is a single track in that language.
- Tracks without a language tag only match other untagged tracks, falling back to the same position.

## Requirements

- Jellyfin 12.x.
- Each user must keep the "remember audio selections" and "remember subtitle selections" playback options enabled. Both are on by default. If one is off, the plugin ignores that part.

## Installation

### From the plugin catalog (recommended)

1. In Jellyfin, go to Dashboard > Plugins > Repositories and add:
   ```
   https://raw.githubusercontent.com/jmarcelocjr/jellyfin-series-track-memory/main/manifest.json
   ```
2. Install "Series Track Memory" from the catalog and restart the server.

Client apps do not need a restart. The plugin runs entirely on the server.

### Manual

```
scripts/package.sh 1.0.0.0 jmarcelocjr/jellyfin-series-track-memory
```

Copy `artifacts/publish/Jellyfin.Plugin.SeriesTrackMemory.dll` to `<jellyfin config dir>/plugins/SeriesTrackMemory_1.0.0.0/` and restart the server.

## Usage

- Anime: in the first episode, pick Japanese audio and your subtitle, then let it play for a few seconds.
- Dubbed cartoons: pick the dubbed audio and turn subtitles off.
- The plugin page in the Dashboard lists learned series per user. "Forget" deletes the preference and clears the selections it wrote.

## Notes

- The jellyfin-web option "Set audio track based on previous item" also picks tracks during continuous playback. Both usually agree. If they conflict, turn that option off.
- Learning uses the player's progress reports, which arrive every few seconds. A track change undone right away may not be recorded.
- The plugin logs "Learned tracks" and "Propagated" lines in the server log.

## Releasing

Push a tag with four version numbers. The workflow runs the tests, builds the zip, publishes the GitHub release and updates `manifest.json`.

```
git tag -a v1.0.0.0 -m "Release 1.0.0.0" && git push origin v1.0.0.0
```

For test builds, add a suffix such as `v0.1.0.0-alpha`. The plugin gets version `0.1.0.0` and the GitHub release is marked as a pre-release, because Jellyfin does not accept suffixes in plugin versions.

## Development

```
dotnet build
dotnet test
```
