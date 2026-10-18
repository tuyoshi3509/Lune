using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lune;

internal static class ImportAlbumSuggestions
{
    private static readonly CompareInfo Names = CultureInfo.GetCultureInfo("ja-JP").CompareInfo;
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    internal static IReadOnlyList<string> Find(IEnumerable<Track> tracks, string artist)
    {
        artist = artist.Trim();
        if (artist.Length == 0 || artist == "不明なアーティスト") return Array.Empty<string>();
        bool Matches(string value) => Names.Compare(value.Trim(), artist, Options) == 0;
        return tracks.Where(t => Matches(t.Artist) || Matches(t.AlbumArtist))
            .Select(t => t.Album.Trim()).Where(name => name.Length > 0 && name != "不明なアルバム")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
