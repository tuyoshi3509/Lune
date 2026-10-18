using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Lune;
public sealed class LibraryService
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".aif", ".aiff", ".ogg", ".opus" };
    public static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..24];
    public static string TrackId(string path) => Id(System.IO.Path.GetFullPath(path));
    public static bool IsAudio(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));
    public static Track ReadTrack(string path, Track? old = null)
    {
        var info = new FileInfo(path);
        if (old != null && old.Modified == info.LastWriteTimeUtc.Ticks && old.Size == info.Length) return old;
        var t = new Track { Id = TrackId(path), Path = System.IO.Path.GetFullPath(path), Title = System.IO.Path.GetFileNameWithoutExtension(path), Favorite = old?.Favorite ?? false, Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks, AddedUtcTicks = old?.AddedUtcTicks ?? DateTime.UtcNow.Ticks, AddedDateEstimated = old?.AddedDateEstimated ?? false };
        using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
        var tag = file.Tag;
        var sourceUrl = DownloadService.ReadSourceUrl(tag.Comment);
        t.SourceUrl = sourceUrl.Length > 0 ? sourceUrl : old?.SourceUrl ?? "";
        t.ImportedFromYouTube = old?.ImportedFromYouTube == true || t.SourceUrl.Length > 0;
        if (!string.IsNullOrWhiteSpace(tag.Title)) t.Title = tag.Title;
        if (tag.Performers.Length > 0) t.Artist = string.Join(" / ", tag.Performers);
        t.AlbumArtist = tag.FirstAlbumArtist ?? "";
        if (!string.IsNullOrWhiteSpace(tag.Album)) t.Album = tag.Album;
        t.Number = tag.Track; t.Disc = tag.Disc; t.Year = tag.Year;
        t.Genre = string.Join(" / ", tag.Genres); t.Seconds = file.Properties.Duration.TotalSeconds;
        // Manual artwork is kept in our cache, never written back to the source music.
        if (old?.Artwork.Contains("custom-") == true && File.Exists(old.Artwork)) t.Artwork = old.Artwork;
        else if (tag.Pictures.FirstOrDefault() is { } picture)
        {
            try { t.Artwork = SaveArtwork(picture.Data.Data); } catch { }
        }
        if (t.Artwork.Length == 0)
        {
            foreach (var name in new[] { "cover.jpg", "folder.jpg", "cover.png", "folder.png", "AlbumArt.jpg" })
            {
                var cover = System.IO.Path.Combine(info.DirectoryName!, name);
                if (!File.Exists(cover)) continue;
                try { t.Artwork = SaveArtwork(File.ReadAllBytes(cover)); break; } catch { }
            }
        }
        return t;
    }
    public static string SaveArtwork(byte[] bytes, bool custom = false)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("画像は32MB以下にしてください。");
        var key = (custom ? "custom-" : "") + Convert.ToHexString(SHA256.HashData(bytes))[..24];
        var path = System.IO.Path.Combine(Paths.Art, key + ".jpg");
        if (File.Exists(path)) return path;
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 320; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var tmp = path + ".tmp";
        using (var output = File.Create(tmp)) encoder.Save(output);
        File.Move(tmp, path, true);
        return path;
    }
    public static async Task<LibraryState> ScanAsync(Settings settings, LibraryState old, IProgress<string> progress, CancellationToken ct)
    {
        return await Task.Run(() => {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var readableRoots = new List<string>();
            var protectedCount = 0; var errors = 0;
            var issues = new List<string>();
            foreach (var root in settings.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) { errors++; continue; }
                var rootOk = true;
                foreach (var p in Enumerate(root, () => { errors++; rootOk = false; }))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsAudio(p)) found.Add(System.IO.Path.GetFullPath(p));
                    else if (System.IO.Path.GetExtension(p).Equals(".m4p", StringComparison.OrdinalIgnoreCase)) protectedCount++;
                }
                if (rootOk) readableRoots.Add(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\");
            }
            foreach (var p in old.ExtraFiles.Where(File.Exists).Where(IsAudio)) found.Add(System.IO.Path.GetFullPath(p));
            var lookup = old.Tracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            var tracks = new List<Track>(found.Count); var index = 0;
            foreach (var path in found)
            {
                ct.ThrowIfCancellationRequested();
                lookup.TryGetValue(path, out var existing);
                try { var t = ReadTrack(path, existing); tracks.Add(t); if (t.ReadIssue.Length > 0) { errors++; issues.Add(path + "\t" + t.ReadIssue); } }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors++;
                    var f = new FileInfo(path);
                    var fallback = existing ?? new Track { Id = TrackId(path), Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path), AddedUtcTicks = DateTime.UtcNow.Ticks };
                    if (fallback.Modified == 0)
                    {
                        fallback.Artist = f.Directory?.Parent?.Name ?? "不明なアーティスト";
                        fallback.Album = f.Directory?.Name ?? "不明なアルバム";
                    }
                    fallback.ReadIssue = ex.Message;
                    // Cache unreadable unchanged files too, so subsequent starts/scans stay inexpensive.
                    try { fallback.Modified = f.LastWriteTimeUtc.Ticks; fallback.Size = f.Length; } catch { }
                    tracks.Add(fallback); issues.Add(path + "\t" + ex.Message);
                }
                if (++index % 40 == 0) progress.Report($"ライブラリを読み込み中… {index:N0} / {found.Count:N0} 曲");
            }
            // Retain entries on temporarily disconnected drives / unreadable folders.
            foreach (var t in old.Tracks.Where(t => !found.Contains(t.Path) && !readableRoots.Any(r => t.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))))
                tracks.Add(t);
            File.WriteAllLines(System.IO.Path.Combine(Paths.Data, "read-issues.tsv"), new[] { "ファイル\t読み取り結果" }.Concat(issues));
            foreach (var folder in tracks.GroupBy(t => System.IO.Path.GetDirectoryName(t.Path), StringComparer.OrdinalIgnoreCase))
            {
                var readable = folder.Where(t => t.ReadIssue.Length == 0).ToList();
                if (readable.Count == 0 || readable.Select(t => t.AlbumKey).Distinct().Count() != 1) continue;
                var reference = readable[0];
                foreach (var t in folder.Where(t => t.ReadIssue.Length > 0)) { t.Album = reference.Album; t.AlbumArtist = reference.AlbumArtist; if (readable.Select(t => t.Artist).Distinct().Count() == 1) t.Artist = reference.Artist; }
            }
            foreach (var album in tracks.GroupBy(t => t.AlbumKey))
            {
                var cover = album.FirstOrDefault(t => t.Artwork.Length > 0)?.Artwork;
                if (cover != null) foreach (var t in album.Where(t => t.Artwork.Length == 0)) t.Artwork = cover;
            }
            return new LibraryState { Tracks = tracks.OrderBy(t => t.Album).ThenBy(t => t.Disc).ThenBy(t => t.Number).ThenBy(t => t.Title).ToList(), Playlists = old.Playlists, ExtraFiles = old.ExtraFiles, ProtectedFiles = protectedCount, ReadErrors = errors, LastScan = DateTime.Now };
        }, ct);
    }
    private static IEnumerable<string> Enumerate(string root, Action error)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop(); string[] files; string[] dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { error(); continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) { try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d); } catch { error(); } }
        }
    }
    public static int ImportM3u(string path, LibraryState state)
    {
        var list = new Playlist { Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        var map = state.Tracks.ToDictionary(t => t.Id);
        foreach (var line in File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !uri.IsFile) continue;
            var local = uri?.IsFile == true ? uri.LocalPath : line;
            var file = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(local) ? local : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, local));
            if (!File.Exists(file) || !IsAudio(file)) continue;
            var id = TrackId(file);
            if (!map.ContainsKey(id)) { try { var t = ReadTrack(file); state.Tracks.Add(t); state.ExtraFiles.Add(file); map[id] = t; } catch { continue; } }
            if (!list.TrackIds.Contains(id)) list.TrackIds.Add(id);
        }
        state.Playlists.Add(list); return list.TrackIds.Count;
    }
    public static (int tracks, int playlists) ImportItunes(string path, LibraryState state)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 100_000_000 });
        var doc = XDocument.Load(reader);
        var root = Dict(doc.Root?.Element("dict"));
        if (!root.TryGetValue("Tracks", out var trackDict)) throw new InvalidDataException("iTunesのライブラリXMLを選択してください。.itl形式には対応していません。");
        var ids = new Dictionary<string, string>();
        var known = state.Tracks.ToDictionary(t => t.Id); int added = 0, playlists = 0;
        foreach (var item in Dict(trackDict))
        {
            var values = Dict(item.Value);
            if (!values.TryGetValue("Location", out var location) || !Uri.TryCreate(location.Value, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            var file = uri.LocalPath;
            if (!File.Exists(file) || !IsAudio(file)) continue;
            var id = TrackId(file); ids[item.Key] = id;
            if (!known.ContainsKey(id))
            {
                try { var t = ReadTrack(file); state.Tracks.Add(t); state.ExtraFiles.Add(file); known[id] = t; added++; } catch { continue; }
            }
        }
        if (root.TryGetValue("Playlists", out var array)) foreach (var element in array.Elements("dict"))
        {
            var values = Dict(element);
            if (values.ContainsKey("Master") || values.ContainsKey("Distinguished Kind") || !values.TryGetValue("Name", out var name) || !values.TryGetValue("Playlist Items", out var items)) continue;
            var source = Id(path) + ":" + (values.GetValueOrDefault("Playlist Persistent ID")?.Value ?? name.Value);
            var list = state.Playlists.FirstOrDefault(p => p.SourceKey == source);
            if (list == null) { list = new Playlist { Name = name.Value, SourceKey = source }; state.Playlists.Add(list); }
            list.TrackIds = items.Elements("dict").Select(Dict).Select(d => d.GetValueOrDefault("Track ID")?.Value ?? "").Where(ids.ContainsKey).Select(i => ids[i]).Distinct().ToList();
            playlists++;
        }
        return (added, playlists);
    }
    private static Dictionary<string, XElement> Dict(XElement? element)
    {
        var result = new Dictionary<string, XElement>(); if (element == null) return result;
        var nodes = element.Elements().ToList();
        for (var i = 0; i + 1 < nodes.Count; i++) if (nodes[i].Name.LocalName == "key") result[nodes[i].Value] = nodes[++i];
        return result;
    }
}
public sealed class ArtworkConverter : IValueConverter
{
    private static readonly Dictionary<string, WeakReference<BitmapImage>> Cache = new();
    private static DrawingImage? placeholder;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = AlbumArtwork.Resolve(value as string ?? "");
        if (path.Length > 0 && File.Exists(path))
        {
            if (Cache.TryGetValue(path, out var weak) && weak.TryGetTarget(out var old)) return old;
            try
            {
                var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.CreateOptions = BitmapCreateOptions.IgnoreImageCache; b.DecodePixelWidth = 256; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                if (Cache.Count > 500) foreach (var key in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList()) Cache.Remove(key);
                Cache[path] = new WeakReference<BitmapImage>(b); return b;
            }
            catch { }
        }
        if (placeholder != null) return placeholder;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(37, 49, 52)), null, new RectangleGeometry(new Rect(0, 0, 256, 256))));
        foreach (var r in new[] { 89, 78, 67, 55 }) group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(63, 79, 80)), 1), new EllipseGeometry(new Point(128, 128), r, r)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(169, 185, 153)), null, new EllipseGeometry(new Point(128, 128), 24, 24)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(37, 49, 52)), null, new EllipseGeometry(new Point(128, 128), 5, 5)));
        placeholder = new DrawingImage(group); placeholder.Freeze(); return placeholder;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
