using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lune;

public sealed record ReleaseUpdate(string Version, string Url, string Hash, long Size, string Notes);
public sealed class UpdateCache
{
    public DateTimeOffset CheckedAt { get; set; }
    public string ETag { get; set; } = "";
    public ReleaseUpdate? Release { get; set; }
}
internal sealed record UpdateFile(string Name, string Hash);
internal sealed record UpdatePlan(int ProcessId, long ProcessStartTicks, string InstallDirectory, string DataDirectory,
    string StageDirectory, string Version, string Receipt, List<UpdateFile> Files);

public sealed class AppUpdates : Observable
{
    public const string Repository = "https://github.com/tuyoshi3509/Lune";
    private const string Endpoint = "https://api.github.com/repos/tuyoshi3509/Lune/releases/latest";
    private const long MaxZip = 64 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly Store store = new();
    private UpdateCache cache;
    private Task? checkTask;
    private bool busy;
    private string status = "";
    internal int RequestCount { get; private set; }
    internal long MetadataBytes { get; private set; }
    internal int CacheWrites { get; private set; }
    internal int LastResponseCode { get; private set; }
    public static Version CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => CurrentVersion.ToString(3);
    public ReleaseUpdate? Available => cache.Release is { } r && ParseVersion(r.Version) > CurrentVersion ? r : null;
    public bool HasUpdate => Available != null;
    public bool Busy { get => busy; private set => Set(ref busy, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string ButtonText => HasUpdate ? $"更新あり · {Available!.Version}" : "アップデート";
    static AppUpdates() { Client.DefaultRequestHeaders.UserAgent.ParseAdd("Lune/" + CurrentText); Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json"); }
    public AppUpdates()
    {
        cache = store.Load<UpdateCache>("update-cache.json");
        try { if (cache.Release != null) ParseVersion(cache.Release.Version); }
        catch { cache = new UpdateCache(); }
        if (cache.CheckedAt > DateTimeOffset.UtcNow) cache.CheckedAt = default;
        Status = "現在のバージョン " + CurrentText;
    }
    internal static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"\Av?\d+\.\d+\.\d+\z") || !Version.TryParse(value.TrimStart('v'), out var parsed))
            throw new InvalidDataException("更新版のバージョンが不正です。");
        return new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
    }
    public Task CheckAsync(bool force, CancellationToken ct)
    {
        if (checkTask is { IsCompleted: false }) return checkTask;
        if (Busy) return Task.CompletedTask;
        if (!force && DateTimeOffset.UtcNow - cache.CheckedAt < TimeSpan.FromHours(24)) { Notify(); return Task.CompletedTask; }
        return checkTask = CheckCore(ct);
    }
    private void Notify() { Raise(nameof(Available)); Raise(nameof(HasUpdate)); Raise(nameof(ButtonText)); }
    private async Task CheckCore(CancellationToken ct)
    {
        Busy = true; Status = "GitHubの更新を確認しています…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            if (cache.Release != null && cache.ETag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);
            RequestCount++;
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            LastResponseCode = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.NotModified) { /* Keep the cached, validated release. */ }
            else if (response.StatusCode == HttpStatusCode.NotFound) { cache.Release = null; cache.ETag = ""; }
            else
            {
                response.EnsureSuccessStatusCode();
                var body = await ReadBounded(response.Content, 2 * 1024 * 1024, timeout.Token);
                MetadataBytes += body.Length;
                using var json = JsonDocument.Parse(body); var root = json.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("正式公開された更新がありません。");
                var tag = root.GetProperty("tag_name").GetString()!; ParseVersion(tag);
                var filename = $"Lune-{tag.TrimStart('v')}-win-x64.zip";
                var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == filename);
                if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("更新ファイルがまだ準備されていません。");
                var url = asset.GetProperty("browser_download_url").GetString()!;
                if (url != Repository + "/releases/download/" + tag + "/" + filename) throw new InvalidDataException("更新ファイルの取得先が不正です。");
                var hash = asset.TryGetProperty("digest", out var digest) ? digest.GetString() ?? "" : "";
                if (!Regex.IsMatch(hash, @"\Asha256:[0-9a-fA-F]{64}\z")) throw new InvalidDataException("SHA256が公開されていないため更新を中止しました。");
                var size = asset.GetProperty("size").GetInt64(); if (size <= 0 || size > MaxZip) throw new InvalidDataException("更新ファイルのサイズが不正です。");
                var notes = root.GetProperty("body").GetString() ?? "";
                cache.Release = new ReleaseUpdate(tag, url, hash[7..], size, notes.Length > 8000 ? notes[..8000] : notes);
                cache.ETag = response.Headers.ETag?.ToString() ?? "";
            }
            cache.CheckedAt = DateTimeOffset.UtcNow; store.Save("update-cache.json", cache); CacheWrites++;
            Status = HasUpdate ? $"{Available!.Version} に更新できます。" : $"最新版です（{CurrentText}）。";
        }
        catch (OperationCanceledException) { Status = "更新確認を中断しました。接続を確認して再試行できます。"; }
        catch (Exception ex) { Status = "更新を確認できませんでした。 " + ex.Message; }
        finally { Busy = false; Notify(); }
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, int max, CancellationToken ct)
    {
        using var memory = new MemoryStream(); await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[32768]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { if (memory.Length + read > max) throw new InvalidDataException("応答サイズが上限を超えています。"); memory.Write(buffer, 0, read); }
        return memory.ToArray();
    }
    internal static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase) {
        "Lune.exe", "lune.portable", "Luneを起動.cmd", "README.md", "CONVENIENCE.md", "THIRD-PARTY-NOTICES.md",
        "licenses/Deno-LICENSE.txt", "licenses/NAudio-LICENSE.txt", "licenses/TagLibSharp-COPYING.txt", "licenses/yt-dlp-LICENSE.txt"
    };
    private static void MaintainStaging()
    {
        var root = Path.Combine(Paths.Data, "updates");
        if (!Directory.Exists(root)) return;
        var completed = new List<DirectoryInfo>(); var pending = 0;
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!Guid.TryParseExact(dir.Name, "N", out _) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try
            {
                var result = Path.Combine(dir.FullName, "result.json");
                if (!File.Exists(result)) { pending++; continue; }
                using var json = JsonDocument.Parse(File.ReadAllText(result));
                if (json.RootElement.TryGetProperty("Success", out var success) && success.GetBoolean()) completed.Add(dir); else pending++;
            }
            catch { pending++; }
        }
        foreach (var dir in completed.OrderByDescending(d => d.LastWriteTimeUtc).Skip(8))
        {
            var full = Path.GetFullPath(dir.FullName);
            if (full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(full, true); } catch { }
        }
        if (pending >= 3) throw new IOException("未完了の更新が3件あります。data/updates内のresult.jsonを確認し、必要なバックアップを保存してから該当フォルダーを整理してください。");
    }
    public async Task<string> PrepareAsync(CancellationToken ct)
    {
        if (Busy) throw new InvalidOperationException("更新の確認・取得が進行中です。");
        var release = Available ?? throw new InvalidOperationException("利用可能な更新がありません。");
        Busy = true; Status = "更新ファイルをダウンロードしています…";
        var receipt = Guid.NewGuid().ToString("N"); var stage = Path.Combine(Paths.Data, "updates", receipt);
        try
        {
            // Recheck a cached release before downloading; only the fixed repository is accepted.
            var tag = release.Version; ParseVersion(tag);
            if (release.Url != Repository + $"/releases/download/{tag}/Lune-{tag.TrimStart('v')}-win-x64.zip" ||
                !Regex.IsMatch(release.Hash, @"\A[0-9a-fA-F]{64}\z") || release.Size <= 0 || release.Size > MaxZip)
                throw new InvalidDataException("保存された更新情報が不正です。再確認してください。");
            MaintainStaging(); Directory.CreateDirectory(stage);
            var zipPath = Path.Combine(stage, "update.zip");
            using (var response = await Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct); await using var output = File.Create(zipPath);
                var bytes = new byte[65536]; long count = 0; int read;
                while ((read = await input.ReadAsync(bytes, ct)) > 0) { count += read; if (count > release.Size) throw new InvalidDataException("サイズ検証に失敗しました。"); await output.WriteAsync(bytes.AsMemory(0, read), ct); }
                if (count != release.Size) throw new InvalidDataException("ダウンロードが完了していません。");
            }
            using (var file = File.OpenRead(zipPath)) if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(release.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256が一致しません。更新を中止しました。");
            var files = Extract(zipPath, stage);
            var version = FileVersionInfo.GetVersionInfo(Path.Combine(stage, "files", "Lune.exe"));
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, 0) != ParseVersion(tag)) throw new InvalidDataException("実行ファイルのバージョンが公開情報と一致しません。");
            var executable = Environment.ProcessPath!;
            if (!Path.GetFileName(executable).Equals("Lune.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("配布版のLune.exeから更新してください。");
            var install = Path.GetDirectoryName(executable)!;
            // Probe write access before closing playback.
            var probe = Path.Combine(install, ".lune-write-" + receipt); using (File.Create(probe)) { } File.Delete(probe);
            using var process = Process.GetCurrentProcess();
            var plan = new UpdatePlan(process.Id, process.StartTime.ToUniversalTime().Ticks, install, Paths.Data, stage, tag, receipt, files);
            var manifest = Path.Combine(stage, "plan.json"); await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(plan), ct);
            using var script = typeof(AppUpdates).Assembly.GetManifestResourceStream("Lune.UpdateHelper.ps1")!;
            using var reader = new StreamReader(script); await File.WriteAllTextAsync(Path.Combine(stage, "apply.ps1"), await reader.ReadToEndAsync(ct), new System.Text.UTF8Encoding(true), ct);
            File.Delete(zipPath); Status = "ダウンロードと検証が完了しました。"; return manifest;
        }
        catch { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } throw; }
        finally { Busy = false; }
    }
    internal static List<UpdateFile> Extract(string archive, string stage)
    {
        var files = new List<UpdateFile>(); long expanded = 0;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 32) throw new InvalidDataException("更新ZIPに予期しない項目があります。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name is "Lune/" or "Lune/licenses/") continue;
            if (!name.StartsWith("Lune/", StringComparison.Ordinal) || !AllowedFiles.Contains(name[5..]) || !seen.Add(name[5..])) throw new InvalidDataException("更新ZIPに許可されていないファイルがあります。");
            expanded += entry.Length; if (expanded > 128 * 1024 * 1024 || entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("展開サイズが上限を超えています。");
            var relative = name[5..]; var target = Path.Combine(stage, "files", relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false);
            using var file = File.OpenRead(target); files.Add(new UpdateFile(relative, Convert.ToHexString(SHA256.HashData(file))));
        }
        if (!seen.Contains("Lune.exe") || !seen.Contains("lune.portable")) throw new InvalidDataException("更新に必要なファイルがありません。");
        return files;
    }
    public static void StartHelper(string manifest)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(manifest)! };
        // A PowerShell 7 parent can export module paths incompatible with Windows PowerShell.
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(start.WorkingDirectory, "apply.ps1"), "-Manifest", manifest }) start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new IOException("更新処理を開始できませんでした。");
    }
}
