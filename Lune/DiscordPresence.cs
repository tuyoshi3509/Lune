using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Lune;

internal sealed record DiscordSong(string Title, string Artist, long Start, long Duration)
{
    internal static DiscordSong? Capture(PlayerService player)
    {
        if (!player.IsPlaying || player.Current == null) return null;
        var position = double.IsFinite(player.Position) ? Math.Max(0, player.Position) : 0;
        var duration = double.IsFinite(player.Duration) ? Math.Max(0, player.Duration) : 0;
        if (duration > 0) position = Math.Min(position, duration);
        return new(Clean(player.Current.Title, "曲名不明"), Clean(player.Current.Artist, "アーティスト不明"),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)position, (long)duration);
    }
    internal bool SameTimeline(DiscordSong? other) => other != null && Title == other.Title && Artist == other.Artist && Math.Abs(Start - other.Start) <= 2 && Duration == other.Duration;
    internal object Payload()
    {
        var activity = new Dictionary<string, object>
        {
            ["name"] = "Lune", ["type"] = 2, ["status_display_type"] = 1,
            ["details"] = Title.Length < 2 ? "♪ " + Title : Title,
            ["state"] = Clean("♪ " + Artist, "♪ アーティスト不明")
        };
        var timestamps = new Dictionary<string, long> { ["start"] = Start };
        if (Duration > 0) timestamps["end"] = Start + Duration;
        activity["timestamps"] = timestamps;
        return activity;
    }
    internal static string Clean(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var next = Rune.IsControl(rune) ? " " : rune.ToString(); var size = Encoding.UTF8.GetByteCount(next);
            if (bytes + size > 128) break;
            result.Append(next); bytes += size;
        }
        return result.ToString();
    }
}

public sealed class DiscordPresence : Observable, IDisposable
{
    private sealed record Desired(bool Enabled, string Id, DiscordSong? Song, bool Stop = false);
    private readonly PlayerService player;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<string, CancellationToken, Task<DiscordIpc>> connect;
    private readonly TimeSpan retry, minimumInterval;
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool suppressed;
    private Desired desired = new(false, "", null);
    private Task? worker;
    private DiscordIpc? active;
    private string status = "オフ", preview = "再生中の曲がここに表示されます。";
    private bool disposed;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Preview { get => preview; private set => Set(ref preview, value); }
    public DiscordPresence(PlayerService player, bool suppressed = false) : this(player, (id, ct) => DiscordIpc.Connect(id, ct), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), suppressed) { }
    internal DiscordPresence(PlayerService player, Func<string, CancellationToken, Task<DiscordIpc>> connect, TimeSpan retry, TimeSpan minimumInterval, bool suppressed = false)
    {
        this.player = player; this.connect = connect; this.retry = retry; this.minimumInterval = minimumInterval; this.suppressed = suppressed;
        player.PropertyChanged += PlayerChanged;
    }
    public static bool ValidId(string? id) => id != null && id.Length is >= 17 and <= 20 && id.All(c => c is >= '0' and <= '9') && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    public void Configure(bool enabled, string id)
    {
        if (disposed) return;
        var song = DiscordSong.Capture(player); UpdatePreview(song);
        var previous = desired;
        desired = new(enabled && !suppressed, id?.Trim() ?? "", song);
        if (suppressed) Status = "検証モード：Discordには送信しません。";
        else if (!enabled) Status = "オフ";
        else if (!ValidId(desired.Id)) Status = "Application IDを入力して保存してください。";
        else if (!previous.Enabled || previous.Id != desired.Id || worker == null) Status = "Discordに接続しています…";
        if (worker == null && desired.Enabled && ValidId(desired.Id)) worker = Task.Run(Run);
        Signal();
    }
    private void PlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || e.PropertyName is not ("Current" or "IsPlaying" or "Position" or "Duration")) return;
        if (!desired.Enabled && e.PropertyName is "Position" or "Duration") return;
        var song = DiscordSong.Capture(player); UpdatePreview(song);
        var old = desired;
        if (old.Song == null && song == null || old.Song?.SameTimeline(song) == true) return;
        desired = old with { Song = song }; Signal();
    }
    private void UpdatePreview(DiscordSong? song) => Preview = song == null ? "再生中の曲がここに表示されます。" : $"♪ {song.Artist}\n{song.Title}";
    private void Signal() { try { changed.Release(); } catch (SemaphoreFullException) { } }
    private void Report(string message, Desired version)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { if (!disposed && desired.Enabled == version.Enabled && desired.Id == version.Id) Status = message; });
    }
    private async Task ClearAndClose(DiscordIpc? connection)
    {
        if (connection == null) return;
        using var timeout = new CancellationTokenSource(450);
        try { if (connection.Connected) await connection.SetActivity(null, timeout.Token).ConfigureAwait(false); }
        catch { /* Closing the IPC session also removes this process's presence. */ }
        finally { connection.Dispose(); }
    }
    private async Task Run()
    {
        string connectedId = ""; DiscordSong? sent = null; var sentOnce = false;
        long lastSend = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var next = Volatile.Read(ref desired);
                if (next.Stop) break;
                if (!next.Enabled || !ValidId(next.Id) || connectedId != next.Id || active?.Connected == false)
                {
                    await ClearAndClose(active).ConfigureAwait(false); active = null; sentOnce = false; connectedId = "";
                }
                if (!next.Enabled || !ValidId(next.Id)) { await changed.WaitAsync(lifetime.Token).ConfigureAwait(false); continue; }
                try
                {
                    if (active == null)
                    {
                        active = await connect(next.Id, lifetime.Token).ConfigureAwait(false); connectedId = next.Id;
                        sentOnce = false; lastSend = 0;
                        // A pause or settings change during the handshake must win over the old song.
                        if (Volatile.Read(ref desired) != next) continue;
                    }
                    var same = sentOnce && (next.Song == null && sent == null || next.Song?.SameTimeline(sent) == true);
                    if (!same)
                    {
                        var elapsed = lastSend == 0 ? minimumInterval : System.Diagnostics.Stopwatch.GetElapsedTime(lastSend);
                        if (next.Song != null && elapsed < minimumInterval)
                        {
                            await changed.WaitAsync(minimumInterval - elapsed, lifetime.Token).ConfigureAwait(false); continue;
                        }
                        await active.SetActivity(next.Song?.Payload(), lifetime.Token).ConfigureAwait(false);
                        sent = next.Song; sentOnce = true; lastSend = System.Diagnostics.Stopwatch.GetTimestamp();
                        Report(sent == null ? "Discordに接続済み・再生待ち（表示なし）" : "再生情報を送信しました。Discord側でアクティビティ共有をオンにしてください。", next);
                    }
                    await changed.WaitAsync(retry, lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                {
                    active?.Dispose(); active = null; connectedId = ""; sentOnce = false;
                    var message = ex is OperationCanceledException ? "Discordから応答がありません。自動で再接続します。" : ex.Message;
                    Report(message + "\nDiscordデスクトップ版を起動し、Application IDを確認してください。", next);
                    await changed.WaitAsync(retry, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { await ClearAndClose(active).ConfigureAwait(false); active = null; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; player.PropertyChanged -= PlayerChanged;
        desired = desired with { Enabled = false, Song = null, Stop = true }; Signal();
        // Bound shutdown even if Discord is hung. All IPC work runs off the WPF thread.
        try { worker?.Wait(650); } catch (AggregateException) { }
        lifetime.Cancel(); active?.Dispose();
    }
}
