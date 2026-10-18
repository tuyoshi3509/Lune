using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lune;

public sealed record YouTubePlaylistInfo(string Id, string Title, IReadOnlyList<PlaylistImportItem> Items);

internal static class YouTubePlaylist
{
    private static string Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2 && p[0] == key).Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault() ?? "";
    internal static string PlaylistId(string url)
    {
        try { var id = Query(new Uri(DownloadService.ValidateUrl(url)), "list"); return Regex.IsMatch(id, @"^[A-Za-z0-9_-]{2,200}$") ? id : ""; }
        catch (ArgumentException) { return ""; }
    }
    internal static string PlaylistUrl(string url)
    {
        var id = PlaylistId(url);
        if (id.Length == 0) throw new ArgumentException("YouTubeの再生リストURL（list=…を含むURL）を入力してください。");
        return "https://www.youtube.com/playlist?list=" + id;
    }
    internal static string VideoId(string url)
    {
        try {
            var uri = new Uri(DownloadService.ValidateUrl(url)); var parts = uri.AbsolutePath.Trim('/').Split('/');
            var id = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ? parts[0] : Query(uri, "v");
            if (id.Length == 0 && parts.Length == 2 && parts[0] is "shorts" or "embed" or "live") id = parts[1];
            return Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$") ? id : "";
        }
        catch (ArgumentException) { return ""; }
    }
    internal static YouTubePlaylistInfo Parse(string json, string url)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("再生リストの曲一覧を取得できませんでした。公開状態とURLを確認してください。");
        var rows = new List<PlaylistImportItem>(); var seen = new HashSet<string>(); var index = 0;
        foreach (var entry in entries.EnumerateArray()) {
            index++;
            if (entry.ValueKind != JsonValueKind.Object) { rows.Add(new PlaylistImportItem(index, new VideoInfo("", "取得できない動画", "", "", "", 0), "非公開・削除済みなど")); continue; }
            var id = Text(entry, "id"); var videoUrl = Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$") ? "https://www.youtube.com/watch?v=" + id : "";
            var title = Text(entry, "title"); var artist = Text(entry, "artist"); if (artist.Length == 0) artist = Text(entry, "uploader"); if (artist.Length == 0) artist = Text(entry, "channel");
            var thumb = Text(entry, "thumbnail");
            if (thumb.Length == 0 && entry.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
                thumb = thumbs.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(t => Text(t, "url")).LastOrDefault() ?? "";
            var duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) && double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
            var unavailable = videoUrl.Length == 0 || title is "[Private video]" or "[Deleted video]" || Text(entry, "availability") is "private" or "premium_only" or "subscriber_only";
            var live = Text(entry, "live_status") is "is_live" or "is_upcoming";
            var reason = unavailable ? "非公開・削除済みなど" : live ? "配信中・配信予定" : !seen.Add(id) ? "リスト内で重複" : "";
            rows.Add(new PlaylistImportItem(index, new VideoInfo(videoUrl, title.Length == 0 ? "タイトル未取得" : title, artist, Text(entry, "album"), thumb, duration), reason));
        }
        if (rows.Count == 0) throw new InvalidDataException("取り込める動画がありません。空の再生リストや非公開の再生リストは取得できません。");
        return new YouTubePlaylistInfo(PlaylistId(url), Text(root, "title") is { Length: > 0 } name ? name : "YouTube 再生リスト", rows);
    }
}

public sealed class PlaylistImportItem : Observable
{
    public int Index { get; }
    public VideoInfo Info { get; private set; }
    public string UnavailableReason { get; }
    public string ExistingTrackId { get; set; } = "";
    public string ImportedTrackId { get; set; } = "";
    public string SavedPath { get; set; } = "";
    public bool DetailsLoaded { get; private set; }
    public string Artwork { get; set; } = "";
    public string OriginalArtwork { get; set; } = "";
    public Int32Rect? CropBounds { get; set; }
    public string ArtworkWarning { get; set; } = "";
    private string title, artist, album, state = "待機";
    private bool titleEdited, artistEdited, albumEdited, selected = true, skipExisting = true;
    public string Title { get => title; set { titleEdited = true; Set(ref title, value); } }
    public string Artist { get => artist; set { artistEdited = true; Set(ref artist, value); } }
    public string Album { get => album; set { albumEdited = true; Set(ref album, value); } }
    public string State { get => state; set => Set(ref state, value); }
    public bool CanSelect => UnavailableReason.Length == 0 && ImportedTrackId.Length == 0 && !(skipExisting && ExistingTrackId.Length > 0);
    public bool Selected { get => selected; set => Set(ref selected, value && CanSelect); }
    public PlaylistImportItem(int index, VideoInfo info, string unavailable = "") { Index = index; Info = info; title = info.Title; artist = info.Artist; album = info.Album; UnavailableReason = unavailable; if (unavailable.Length > 0) { selected = false; state = unavailable; } }
    public void RefreshAvailability(bool skip)
    {
        skipExisting = skip; Raise(nameof(CanSelect));
        if (!CanSelect) Selected = false;
        if (ImportedTrackId.Length > 0) State = "登録完了" + (ArtworkWarning.Length > 0 ? "（画像なし）" : "");
        else if (UnavailableReason.Length > 0) State = UnavailableReason;
        else if (skip && ExistingTrackId.Length > 0) State = "登録済み・スキップ";
        else if (State == "登録済み・スキップ") State = "待機";
    }
    public void ApplyMetadata(VideoInfo info)
    {
        Info = info; DetailsLoaded = true;
        if (!titleEdited) { title = info.Title; Raise(nameof(Title)); }
        if (!artistEdited) { artist = info.Artist; Raise(nameof(Artist)); }
        if (!albumEdited) { album = info.Album; Raise(nameof(Album)); }
    }
}

internal sealed record BatchImportResult(int Imported, int Failed, bool Cancelled);
internal static class BatchImportRunner
{
    internal static async Task<BatchImportResult> RunAsync(IEnumerable<PlaylistImportItem> items, Func<PlaylistImportItem, CancellationToken, Task<Track>> import, Action<PlaylistImportItem> saved, CancellationToken ct)
    {
        var count = 0; var failed = 0;
        foreach (var item in items.Where(i => i.Selected && i.CanSelect).ToList()) {
            if (ct.IsCancellationRequested) return new(count, failed, true);
            try {
                item.State = "取り込み中";
                var track = await import(item, ct); item.ImportedTrackId = track.Id; item.RefreshAvailability(true); count++;
                saved(item);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { item.State = "中断・再試行できます"; return new(count, failed, true); }
            catch (Exception ex) when (item.ImportedTrackId.Length == 0) { failed++; item.State = "失敗: " + ex.Message.Replace('\r', ' ').Replace('\n', ' '); }
        }
        return new(count, failed, ct.IsCancellationRequested);
    }
}
