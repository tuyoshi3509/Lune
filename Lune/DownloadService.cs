using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lune;
public sealed record VideoInfo(string Url, string Title, string Artist, string Album, string Thumbnail, double Seconds);
public static class DownloadService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private static readonly ConcurrentDictionary<int, Process> ActiveProcesses = new();
    internal static int[] ActiveProcessIds => ActiveProcesses.Keys.ToArray();
    public static void StopAll()
    {
        foreach (var process in ActiveProcesses.Values)
        {
            try { if (!process.HasExited) process.Kill(true); process.WaitForExit(3000); } catch { }
        }
    }
    public static bool ToolsReady => new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe" }.All(n => File.Exists(Path.Combine(Paths.Tools, n)));
    static DownloadService() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Lune-Music-Player/1.0"); }
    public static string ValidateUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !(uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("https://www.youtube.com/ または https://youtu.be/ の動画URLを入力してください。");
        return uri.AbsoluteUri;
    }
    public static async Task InstallToolsAsync(IProgress<string> progress, CancellationToken ct, bool update = false)
    {
        Directory.CreateDirectory(Paths.Tools);
        if (update || !File.Exists(Path.Combine(Paths.Tools, "yt-dlp.exe"))) await GitHubAsset("yt-dlp/yt-dlp", "yt-dlp.exe", "yt-dlp.exe", progress, ct);
        if (!File.Exists(Path.Combine(Paths.Tools, "deno.exe"))) await GitHubAsset("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", "deno.exe", progress, ct);
        if (!File.Exists(Path.Combine(Paths.Tools, "ffmpeg.exe")) || !File.Exists(Path.Combine(Paths.Tools, "ffprobe.exe")))
        {
            const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            var hash = (await Http.GetStringAsync(url + ".sha256", ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var path = Path.Combine(Paths.Tools, "ffmpeg-package.zip");
            await Download(url, path, "FFmpeg", progress, ct);
            Verify(path, hash);
            using (var zip = ZipFile.OpenRead(path))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var tmp = Path.Combine(Paths.Tools, name + ".new"); entry.ExtractToFile(tmp, true); File.Move(tmp, Path.Combine(Paths.Tools, name), true);
                }
                foreach (var entry in zip.Entries.Where(e => e.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)))
                    entry.ExtractToFile(Path.Combine(Paths.Tools, "FFmpeg-LICENSE.txt"), true);
            }
            File.AppendAllText(Path.Combine(Paths.Tools, "sources.txt"), $"FFmpeg {url} SHA256 {hash}\n"); File.Delete(path);
        }
        progress.Report("取り込みツールの準備が完了しました。");
    }
    private static async Task GitHubAsset(string repo, string assetName, string executable, IProgress<string> progress, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct));
        var asset = json.RootElement.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString() == assetName);
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString()?.Replace("sha256:", "") : null;
        if (string.IsNullOrWhiteSpace(digest) && repo == "yt-dlp/yt-dlp")
        {
            var sums = json.RootElement.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString() == "SHA2-256SUMS");
            var text = await Http.GetStringAsync(sums.GetProperty("browser_download_url").GetString()!, ct);
            digest = text.Split('\n').First(l => l.TrimEnd().EndsWith(" " + assetName, StringComparison.Ordinal)).Split(' ')[0];
        }
        if (string.IsNullOrWhiteSpace(digest)) throw new InvalidDataException("公開されたSHA256が取得できません。時間をおいて再試行してください。");
        var path = Path.Combine(Paths.Tools, assetName + ".download");
        await Download(url, path, executable, progress, ct); Verify(path, digest);
        if (assetName.EndsWith(".zip"))
        {
            using (var zip = ZipFile.OpenRead(path)) zip.Entries.First(e => e.Name == executable).ExtractToFile(Path.Combine(Paths.Tools, executable + ".new"), true);
            File.Move(Path.Combine(Paths.Tools, executable + ".new"), Path.Combine(Paths.Tools, executable), true); File.Delete(path);
        }
        else File.Move(path, Path.Combine(Paths.Tools, executable), true);
        File.AppendAllText(Path.Combine(Paths.Tools, "sources.txt"), $"{repo} {json.RootElement.GetProperty("tag_name").GetString()} {url} SHA256 {digest}\n");
    }
    private static void Verify(string path, string expected)
    {
        using var file = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ダウンロードの検証に失敗しました。再試行してください。");
    }
    private static async Task Download(string url, string path, string label, IProgress<string> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); await using var file = File.Create(path);
        var buffer = new byte[131072]; long total = 0; var clock = Stopwatch.StartNew(); int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, count), ct); total += count;
            if (clock.ElapsedMilliseconds > 600) { progress.Report($"{label} を準備中… {total / 1048576d:F1} MB"); clock.Restart(); }
        }
    }
    private static List<string> Common(bool playlist = false) => new() { "--ignore-config", playlist ? "--yes-playlist" : "--no-playlist", "--no-color", "--encoding", "utf-8", "--socket-timeout", "25", "--retries", "2", "--js-runtimes", "deno:" + Path.Combine(Paths.Tools, "deno.exe"), "--ffmpeg-location", Paths.Tools };
    public static async Task<YouTubePlaylistInfo> FetchPlaylistAsync(string url, CancellationToken ct)
    {
        url = YouTubePlaylist.PlaylistUrl(url); var args = Common(true);
        args.AddRange(new[] { "--flat-playlist", "--dump-single-json", "--skip-download", "--ignore-errors", "--", url });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        return YouTubePlaylist.Parse(await Run(Path.Combine(Paths.Tools, "yt-dlp.exe"), args, null, timeout.Token), url);
    }
    public static async Task<VideoInfo> FetchAsync(string url, CancellationToken ct)
    {
        url = ValidateUrl(url); var args = Common(); args.AddRange(new[] { "--dump-single-json", "--skip-download", "--", url });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var output = await Run(Path.Combine(Paths.Tools, "yt-dlp.exe"), args, null, timeout.Token);
        using var json = JsonDocument.Parse(output); var root = json.RootElement;
        string Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (Get("live_status") is "is_live" or "is_upcoming") throw new InvalidOperationException("配信中・配信予定の動画は取り込めません。配信終了後のアーカイブを指定してください。");
        var title = Get("track"); if (title.Length == 0) title = Get("title");
        var artist = Get("artist"); if (artist.Length == 0) artist = Get("uploader");
        var videoId = YouTubePlaylist.VideoId(Get("webpage_url")); if (videoId.Length > 0) url = "https://www.youtube.com/watch?v=" + videoId;
        return new VideoInfo(url, title, artist, Get("album"), Get("thumbnail"), root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number && du.TryGetDouble(out var secs) ? secs : 0);
    }
    public static async Task<string> ImportAsync(VideoInfo info, string title, string artist, string album, string format, string art, string folder, IProgress<string> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("曲名を入力してください。");
        if (format is not ("m4a" or "mp3")) throw new ArgumentException("形式が不正です。");
        ValidateUrl(info.Url);
        var staging = Path.Combine(Paths.Data, "imports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            var args = Common(); args.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", format, "--audio-quality", "0", "--newline", "--no-mtime", "--output", Path.Combine(staging, "audio.%(ext)s"), "--postprocessor-args", "ffmpeg:-threads 1", "--", info.Url });
            await Run(Path.Combine(Paths.Tools, "yt-dlp.exe"), args, progress, ct);
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(staging, "audio." + format);
            if (!File.Exists(output)) throw new IOException("変換された音楽ファイルが見つかりません。");
            WriteImportTags(output, title, artist, album, art, info.Url);
            ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(folder);
            var stem = SafeName(artist + " - " + title); var destination = Path.Combine(folder, stem + "." + format); var n = 2;
            while (File.Exists(destination)) destination = Path.Combine(folder, stem + $" ({n++})." + format);
            File.Move(output, destination); return destination;
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }
    internal static void WriteImportTags(string path, string title, string artist, string album, string art, string sourceUrl)
    {
        sourceUrl = ValidateUrl(sourceUrl);
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title.Trim(); file.Tag.Performers = new[] { artist.Trim() }; file.Tag.AlbumArtists = new[] { artist.Trim() }; file.Tag.Album = album.Trim();
        if (File.Exists(art)) file.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(art) { Type = TagLib.PictureType.FrontCover } };
        var originalComment = string.Join("\n", (file.Tag.Comment ?? "").Split('\n').Where(line => !line.StartsWith("Lune source URL: ", StringComparison.Ordinal)));
        file.Tag.Comment = (originalComment.Length > 0 ? originalComment.TrimEnd() + "\n" : "") + "Lune source URL: " + sourceUrl;
        file.Save();
    }
    internal static string ReadSourceUrl(string? comment)
    {
        const string prefix = "Lune source URL: ";
        var line = (comment ?? "").Split('\n').LastOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        if (line == null) return "";
        try { return ValidateUrl(line[prefix.Length..].Trim()); } catch (ArgumentException) { return ""; }
    }
    public static string SafeName(string value)
    {
        var result = new string(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (result.Length > 140) result = result[..140].TrimEnd('.');
        if (string.IsNullOrEmpty(result)) result = "Audio";
        if (Regex.IsMatch(result, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase)) result = "_" + result;
        return result;
    }
    public static async Task<string> FetchArtAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return "";
        var bytes = await Http.GetByteArrayAsync(uri, ct); return ArtworkCrop.CacheSource(bytes);
    }
    public static async Task<string> Run(string exe, IEnumerable<string> args, IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("取り込みツールを準備してください。", exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Paths.Tools };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("取り込み処理を開始できませんでした。");
        var processId = process.Id; ActiveProcesses[processId] = process;
        try
        {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var output = new StringBuilder(); var errors = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder buffer)
        {
            while (await reader.ReadLineAsync(ct) is { } line) { buffer.AppendLine(line); if (progress != null && line.Length > 0) progress.Report(line); }
        }
        await Task.WhenAll(Read(process.StandardOutput, output), Read(process.StandardError, errors), process.WaitForExitAsync(ct));
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) { var message = errors.Length > 0 ? errors.ToString() : output.ToString(); throw new IOException(message.Length > 1800 ? message[^1800..] : message); }
        return output.ToString();
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } } catch { }
            ActiveProcesses.TryRemove(processId, out _);
        }
    }
}
