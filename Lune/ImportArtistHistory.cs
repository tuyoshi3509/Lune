using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lune;

public sealed class ImportArtistChoice
{
    public string SourceArtist { get; set; } = "";
    public string Artist { get; set; } = "";
    public long UsedUtcTicks { get; set; }
}

public sealed class ImportArtistHistory
{
    private readonly Store store = new();
    private readonly List<ImportArtistChoice> choices;
    public ImportArtistHistory() => choices = store.Load<List<ImportArtistChoice>>("import-artists.json");
    public string? Warning { get; private set; }
    public IReadOnlyList<string> Suggestions(IEnumerable<Track> tracks, string sourceArtist)
    {
        var history = choices.OrderByDescending(c => c.UsedUtcTicks).ToList();
        return history.Where(c => sourceArtist.Trim().Length > 0 && c.SourceArtist.Equals(sourceArtist.Trim(), StringComparison.OrdinalIgnoreCase)).Select(c => c.Artist)
            .Concat(history.Select(c => c.Artist))
            .Concat(tracks.Where(t => t.IsYouTube).OrderByDescending(t => t.AddedUtcTicks).Select(t => t.Artist))
            .Select(a => a.Trim()).Where(a => a.Length > 0 && a != "不明なアーティスト")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void Remember(string sourceArtist, string artist)
    {
        sourceArtist = sourceArtist.Trim(); artist = artist.Trim();
        if (artist.Length == 0 || artist == "不明なアーティスト") return;
        choices.RemoveAll(c => c.SourceArtist.Equals(sourceArtist, StringComparison.OrdinalIgnoreCase) && c.Artist.Equals(artist, StringComparison.OrdinalIgnoreCase));
        choices.Add(new ImportArtistChoice { SourceArtist = sourceArtist, Artist = artist, UsedUtcTicks = DateTime.UtcNow.Ticks });
        try { store.Save("import-artists.json", choices); Warning = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "アーティスト候補の履歴を保存できませんでした: " + ex.Message; }
    }
}
