using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Lune;

internal static class AlbumArtwork
{
    // Artwork cache filenames are content hashes, so they survive moving a portable install.
    // Only resolve our own cache names; never substitute unrelated external images.
    internal static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            var name = Path.GetFileName(path);
            if (Regex.IsMatch(name, @"\A(?:custom-)?[0-9A-F]{24}\.jpg\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var local = Path.Combine(Paths.Art, name);
                if (File.Exists(local)) return local;
            }
        }
        catch (System.ArgumentException) { }
        return path;
    }
    // Rebuild only after library changes, never on playback ticks or navigation.
    // Display fallbacks do not modify tags or persisted per-track artwork.
    internal static void Refresh(IEnumerable<Track> tracks, Track? current = null)
    {
        var covers = new Dictionary<string, string>();
        var resolved = new Dictionary<string, string>();
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Artwork)) continue;
            if (!resolved.TryGetValue(track.Artwork, out var path))
                resolved[track.Artwork] = path = Resolve(track.Artwork);
            track.Artwork = path;
            covers.TryAdd(track.AlbumKey, path);
        }
        foreach (var track in tracks) track.SetAlbumArtwork(covers.GetValueOrDefault(track.AlbumKey, ""));
        if (current != null) current.SetAlbumArtwork(covers.GetValueOrDefault(current.AlbumKey, ""));
    }
}
