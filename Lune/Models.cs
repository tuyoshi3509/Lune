using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Lune;
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
}
public sealed class Track : Observable
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "不明なアーティスト";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "不明なアルバム";
    public string Genre { get; set; } = "";
    public uint Year { get; set; }
    public uint Number { get; set; }
    public uint Disc { get; set; }
    public double Seconds { get; set; }
    private string artwork = "";
    public string Artwork { get => artwork; set { if (Set(ref artwork, value)) Raise(nameof(DisplayArtwork)); } }
    private string albumArtwork = "";
    [JsonIgnore] public string DisplayArtwork => string.IsNullOrWhiteSpace(Artwork) ? albumArtwork : Artwork;
    internal void SetAlbumArtwork(string value) { if (albumArtwork == value) return; albumArtwork = value; Raise(nameof(DisplayArtwork)); }
    public long Modified { get; set; }
    public long AddedUtcTicks { get; set; }
    public bool AddedDateEstimated { get; set; }
    public long Size { get; set; }
    public string ReadIssue { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public bool ImportedFromYouTube { get; set; }
    [JsonIgnore] public bool IsYouTube => ImportedFromYouTube || SourceUrl.Length > 0;
    [JsonIgnore] public bool HasSourceUrl => SourceUrl.Length > 0;
    [JsonIgnore] public string SourceHint => HasSourceUrl ? "元動画を開く" : "URLの記録開始前に取り込んだ曲です。元動画のURLは保存されていません。";
    [JsonIgnore] public string DisplayTitle => (ReadIssue.Length > 0 ? "⚠  " : "") + Title;
    private bool favorite;
    public bool Favorite { get => favorite; set { if (Set(ref favorite, value)) Raise(nameof(Heart)); } }
    [JsonIgnore] public string Heart => Favorite ? "♥" : "♡";
    [JsonIgnore] public string Duration => TimeSpan.FromSeconds(Math.Max(0, Seconds)).ToString(Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    [JsonIgnore] public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    [JsonIgnore] public string AlbumKey => (string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist) + "\u001f" + Album;
}
public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "マイリスト";
    public List<string> TrackIds { get; set; } = new();
    public string SourceKey { get; set; } = "";
    public bool ScheduleEnabled { get; set; }
    public bool ScheduleLoop { get; set; } = true;
    public double ScheduleFadeSeconds { get; set; } = 5;
    public List<PlaylistTimeRange> TimeRanges { get; set; } = new();
    public override string ToString() => Name;
}
public sealed class Settings
{
    public List<string> Folders { get; set; } = new();
    public double Volume { get; set; } = 0.15;
    public double VolumeMinimum { get; set; }
    public double VolumeMaximum { get; set; } = 0.3;
    public bool Muted { get; set; }
    public bool SpectrumEnabled { get; set; }
    public string OutputDeviceId { get; set; } = "";
    public string SecondaryOutputDeviceId { get; set; } = AudioRoutes.Off;
    public bool PrimaryOutputMuted { get; set; }
    public bool SecondaryOutputMuted { get; set; }
    public bool DiscordEnabled { get; set; }
    public string DiscordApplicationId { get; set; } = "";
    public string AlbumSort { get; set; } = "nameAsc";
    public string ArtistSort { get; set; } = "nameAsc";
    public string AlbumViewMode { get; set; } = "grid";
    public string ArtistViewMode { get; set; } = "grid";
    public string TrackSort { get; set; } = "default";
    public string PlaylistSort { get; set; } = "default";
    public double Opacity { get; set; } = 1;
    public string Accent { get; set; } = "#CBEB9B";
    public string Theme { get; set; } = "Charcoal";
    public bool MiniTopmost { get; set; } = true;
    public bool MediaKeys { get; set; } = true;
    public bool StartMini { get; set; }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    public string LastTrack { get; set; } = "";
    public double LastPosition { get; set; }
    public List<string> Queue { get; set; } = new();
    public List<string> ExplicitQueue { get; set; } = new();
    public bool QueuePlayback { get; set; }
    public string LastScheduledPlaylistId { get; set; } = "";
    public string DownloadFolder { get; set; } = "";
    public double MiniLeft { get; set; } = -1;
    public double MiniTop { get; set; } = -1;
}
public sealed class LibraryState
{
    public List<Track> Tracks { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
    public List<string> ExtraFiles { get; set; } = new();
    public int ProtectedFiles { get; set; }
    public int ReadErrors { get; set; }
    public DateTime? LastScan { get; set; }
}
public sealed class AlbumCard
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Artwork { get; set; } = "";
    public List<Track> Tracks { get; set; } = new();
    public bool IsArtist { get; set; }
    public long AddedUtcTicks => Tracks.Count == 0 ? 0 : Tracks.Min(t => t.AddedUtcTicks);
}
public static class Paths
{
    public static string Root { get; private set; } = "";
    public static string Data { get; private set; } = "";
    public static string Art => System.IO.Path.Combine(Data, "artwork");
    public static string Tools => System.IO.Path.Combine(Root, "tools");
    public static void Initialize(string[] args)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir.Parent != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "lune.portable"))) dir = dir.Parent;
        Root = File.Exists(System.IO.Path.Combine(dir.FullName, "lune.portable")) ? dir.FullName : AppContext.BaseDirectory;
        var n = Array.IndexOf(args, "--data");
        Data = n >= 0 && n + 1 < args.Length ? System.IO.Path.GetFullPath(args[n + 1]) : System.IO.Path.Combine(Root, "data");
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Art); Directory.CreateDirectory(Tools);
    }
}
public sealed class Store
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public string? Warning { get; private set; }
    public T Load<T>(string name) where T : new()
    {
        var path = System.IO.Path.Combine(Paths.Data, name);
        if (!File.Exists(path)) return new T();
        try { using var file = File.OpenRead(path); return JsonSerializer.Deserialize<T>(file, Options) ?? new T(); }
        catch
        {
            File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            Warning = "保存データを読み込めませんでした。バックアップから復元しました。";
            try { using var backup = File.OpenRead(path + ".bak"); return JsonSerializer.Deserialize<T>(backup, Options) ?? new T(); }
            catch { Warning = "保存データを読み込めませんでした。破損ファイルを退避しました。"; return new T(); }
        }
    }
    public void Save<T>(string name, T data)
    {
        var path = System.IO.Path.Combine(Paths.Data, name); var tmp = path + ".tmp";
        using (var file = File.Create(tmp)) JsonSerializer.Serialize(file, data, Options);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
    }
}
public static class SearchService
{
    public static IEnumerable<Track> Filter(IEnumerable<Track> tracks, string query, int field)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return tracks;
        return tracks.Where(t => words.All(w => Match(t, w, field)));
    }
    private static bool Match(Track t, string word, int field)
    {
        bool Has(string s) => System.Globalization.CultureInfo.CurrentCulture.CompareInfo.IndexOf(s, word,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreWidth | System.Globalization.CompareOptions.IgnoreKanaType) >= 0;
        return field switch { 1 => Has(t.Title), 2 => Has(t.Album), 3 => Has(t.Artist) || Has(t.AlbumArtist), _ => Has(t.Title) || Has(t.Album) || Has(t.Artist) || Has(t.AlbumArtist) };
    }
}
