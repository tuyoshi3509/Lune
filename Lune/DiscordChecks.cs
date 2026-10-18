using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using NAudio.Wave;

namespace Lune;

internal static class DiscordChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var log = Path.Combine(Paths.Root, "test-output", "discord-tests.txt");
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        async Task Wait(Func<bool> condition, string name)
        {
            var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 8) await Task.Delay(35);
            Check(condition(), name);
        }
        try
        {
            Check(!new Settings().DiscordEnabled && new Settings().DiscordApplicationId == "", "Discord sharing defaults off and does not invent an application ID");
            Check(!JsonSerializer.Deserialize<Settings>("{\"Volume\":0.1}")!.DiscordEnabled, "old settings migrate with sharing disabled");
            const string appId = "123456789012345678";
            Check(DiscordPresence.ValidId(appId) && !DiscordPresence.ValidId("mfa.secret-token") && !DiscordPresence.ValidId("abc") && !DiscordPresence.ValidId("99999999999999999999"), "only valid numeric application IDs accepted");
            var clean = DiscordSong.Clean(string.Concat(Enumerable.Repeat("日本語🎵", 80)), "fallback");
            Check(Encoding.UTF8.GetByteCount(clean) <= 128 && !clean.Contains('�'), "long Japanese and emoji labels truncate without breaking UTF-8");
            Check(DiscordSong.Clean("a\nb\t", "x") == "a b", "metadata control characters flattened");
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(new DiscordSong("曲", "歌手", 100, 0).Payload())))
                Check(!json.RootElement.GetProperty("timestamps").TryGetProperty("end", out _) && json.RootElement.GetProperty("details").GetString() == "♪ 曲", "unknown duration omits end timestamp and short titles remain valid");

            main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Preferences.Folders.Clear();
            main.P.Muted = true; main.P.SecondaryOutputDeviceId = AudioRoutes.Off; main.P.Repeat = 0;
            var path = Path.Combine(Paths.Data, "discord-silent.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2))) writer.Write(new byte[44100 * 4 * 30], 0, 44100 * 4 * 30);
            var track = new Track { Id = "discord-fixture", Path = path, Title = "夜空のメロディ", Artist = "テストアーティスト", Album = "テストアルバム", Seconds = 30, Artwork = Path.Combine(Paths.Data, "private-art.jpg"), SourceUrl = "https://www.youtube.com/watch?v=private-source" };
            main.Library.Tracks.Add(track); main.P.PlayCollection(new[] { track });
            await Wait(() => main.P.IsPlaying && main.P.Position > .1, "silent local playback active for integration tests");
            using var server = new FakeDiscord(); var attempts = 0;
            using var presence = new DiscordPresence(main.P, (id, ct) => { Interlocked.Increment(ref attempts); return DiscordIpc.Connect(id, ct, new[] { server.Name }); }, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500));
            presence.Configure(false, appId); await Task.Delay(100);
            Check(attempts == 0, "disabled integration does not open a pipe or poll Discord");
            presence.Configure(true, "invalid"); await Task.Delay(100);
            Check(attempts == 0 && presence.Status.Contains("Application ID"), "missing/invalid ID stays offline with setup guidance");
            presence.Configure(true, appId);
            await Wait(() => server.Activities.Any(a => a.Json.ValueKind == JsonValueKind.Object), "local IPC handshake and SET_ACTIVITY acknowledged");
            var first = server.Activities.First(a => a.Json.ValueKind == JsonValueKind.Object);
            Check(server.ClientId == appId, "handshake uses configured application ID");
            Check(first.Json.GetProperty("type").GetInt32() == 2 && first.Json.GetProperty("status_display_type").GetInt32() == 1, "listening activity requests artist status in member list");
            Check(first.Json.GetProperty("state").GetString() == "♪ テストアーティスト" && first.Json.GetProperty("details").GetString() == track.Title, "artist and track name are sent as requested");
            var stamps = first.Json.GetProperty("timestamps");
            Check(stamps.GetProperty("end").GetInt64() - stamps.GetProperty("start").GetInt64() == 30, "start/end timestamps represent track duration");
            var raw = first.Json.GetRawText();
            Check(!raw.Contains("private-art") && !raw.Contains("private-source") && !raw.Contains("discord-silent") && !raw.Contains("token"), "local paths artwork and source URLs are not shared");
            await Wait(() => server.Pongs > 0, "IPC ping answered while idle and split frames handled");
            var before = server.Activities.Count; await Task.Delay(700);
            Check(server.Activities.Count == before, "ordinary position ticks do not resend identical presence");
            main.P.Seek(7); main.P.Seek(11);
            await Wait(() => server.Activities.Count > before, "seek updates Discord's playback timeline");
            await Wait(() => Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - server.Activities.Last(a => a.Json.ValueKind == JsonValueKind.Object).Json.GetProperty("timestamps").GetProperty("start").GetInt64() - main.P.Position) < 2, "latest seek wins over intermediate positions");
            var activeTimes = server.Activities.Where(a => a.Json.ValueKind == JsonValueKind.Object).Select(a => a.At).ToArray();
            Check(activeTimes.Zip(activeTimes.Skip(1), (a, b) => b - a).All(d => d.TotalMilliseconds >= 450), "active updates respect minimum send interval");
            main.P.Toggle();
            await Wait(() => server.Activities.Last().Json.ValueKind == JsonValueKind.Null, "pause clears Discord presence");
            main.P.Toggle();
            await Wait(() => server.Activities.Last().Json.ValueKind == JsonValueKind.Object, "resume restores current track presence");
            var nextTrack = new Track { Id = "discord-next", Path = path, Title = "次の曲", Artist = "別のアーティスト", Seconds = 30 };
            main.P.PlayCollection(new[] { nextTrack });
            await Wait(() => server.Activities.Last().Json is var activity && activity.ValueKind == JsonValueKind.Object && activity.GetProperty("details").GetString() == nextTrack.Title && activity.GetProperty("state").GetString() == "♪ 別のアーティスト", "track changes update both artist and title");
            main.P.Stop();
            await Wait(() => server.Activities.Last().Json.ValueKind == JsonValueKind.Null, "stop clears Discord activity");
            main.P.PlayCollection(new[] { track });
            await Wait(() => server.Activities.Last().Json is var activity && activity.ValueKind == JsonValueKind.Object && activity.GetProperty("details").GetString() == track.Title, "playback after stop publishes the current song");
            var sessions = server.Sessions; var priorActivities = server.Activities.Count; server.Disconnect();
            await Wait(() => server.Sessions > sessions && server.Activities.Count > priorActivities && server.Activities.Last().Json.ValueKind == JsonValueKind.Object, "Discord restart reconnects and resends current song");
            await Wait(() => presence.Status.Contains("送信しました"), "connection status reflects acknowledged activity");
            presence.Configure(true, appId);
            Check(presence.Status.Contains("送信しました"), "saving unchanged settings preserves connected status");
            presence.Configure(false, appId);
            await Wait(() => server.Activities.Last().Json.ValueKind == JsonValueKind.Null, "turning off sharing clears the activity");
            var oldAttempts = attempts; await Task.Delay(400); Check(attempts == oldAttempts, "disabled integration stops reconnect attempts");
            presence.Configure(true, appId);
            await Wait(() => server.Activities.Last().Json.ValueKind == JsonValueKind.Object, "integration can be re-enabled without restarting player");
            var shutdown = Stopwatch.StartNew(); presence.Dispose();
            Check(shutdown.Elapsed.TotalMilliseconds < 1000 && server.Activities.Last().Json.ValueKind == JsonValueKind.Null, "shutdown sends clear and completes within bounded time");
            main.P.Stop();
            using (var direct = await DiscordIpc.Connect(appId, CancellationToken.None, new[] { server.Name }))
            {
                server.RejectNext = true; var rejected = false;
                try { await direct.SetActivity(new DiscordSong("song", "artist", 100, 30).Payload(), CancellationToken.None); }
                catch (IOException ex) { rejected = ex.Message.Contains("4000"); }
                Check(rejected, "Discord error acknowledgements surface a useful error instead of success");
            }
            using (var bad = new FakeDiscord { OversizedHandshake = true })
            {
                var rejected = false;
                try { using var direct = await DiscordIpc.Connect(appId, CancellationToken.None, new[] { bad.Name }); }
                catch (IOException) { rejected = true; }
                Check(rejected, "malformed oversized IPC frames rejected before allocation");
            }
            main.Preferences.DiscordEnabled = false; main.Preferences.DiscordApplicationId = "";
            main.Discord.Configure(false, "");
            var settings = new SettingsWindow(main); settings.Show();
            var panel = settings.DiscordPanel; panel.ApplicationIdInput.Text = appId;
            panel.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); panel.EnabledCheck.IsChecked = true;
            var saved = new Store().Load<Settings>("settings.json");
            Check(saved.DiscordApplicationId == appId && saved.DiscordEnabled, "settings save application ID and sharing toggle");
            panel.ApplicationIdInput.Text = "not-a-client-secret"; panel.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(main.Preferences.DiscordApplicationId == appId, "invalid ID text is not persisted over valid settings");
            panel.EnabledCheck.IsChecked = false;
            Check(!main.Preferences.DiscordEnabled, "off toggle works even during an invalid ID edit");
            panel.ApplicationIdInput.Text = ""; panel.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var parent = (System.Windows.Controls.StackPanel)panel.Parent;
            var scroll = (System.Windows.Controls.ScrollViewer)parent.Parent;
            scroll.ScrollToVerticalOffset(panel.TranslatePoint(new Point(), parent).Y);
            await Task.Delay(150); SelfTests.Capture(settings, "lune-discord-settings.png");
            scroll.ScrollToVerticalOffset(panel.TranslatePoint(new Point(), parent).Y + 290);
            await Task.Delay(150); SelfTests.Capture(settings, "lune-discord-setup.png");
            Check(main.Discord.Status.Contains("検証モード"), "verification mode prevents accidental public Discord activity");
            settings.Close();
            results.Add("Actual Discord profile display not tested: user-owned Application ID is not created yet.");
            File.WriteAllLines(log, results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(log, results); main.Discord.Dispose(); Application.Current.Shutdown(1); }
    }

    private sealed class FakeDiscord : IDisposable
    {
        internal string Name { get; } = "lune-discord-test-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource stop = new();
        private NamedPipeServerStream? current;
        internal readonly ConcurrentQueue<(JsonElement Json, DateTimeOffset At)> Activities = new();
        internal string ClientId = "";
        internal int Sessions, Pongs;
        internal volatile bool RejectNext, OversizedHandshake;
        internal FakeDiscord() { _ = Task.Run(Run); }
        internal void Disconnect() { try { current?.Dispose(); } catch { } }
        private async Task Run()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    current = pipe; await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false); Interlocked.Increment(ref Sessions);
                    while (pipe.IsConnected && !stop.IsCancellationRequested)
                    {
                        var header = new byte[8]; await pipe.ReadExactlyAsync(header, stop.Token).ConfigureAwait(false);
                        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header); var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                        if (length < 0 || length > 1024 * 1024) throw new IOException("Bad test frame");
                        var body = new byte[length]; await pipe.ReadExactlyAsync(body, stop.Token).ConfigureAwait(false);
                        if (opcode == 4) { Interlocked.Increment(ref Pongs); continue; }
                        using var doc = JsonDocument.Parse(body); var root = doc.RootElement;
                        if (opcode == 0)
                        {
                            ClientId = root.GetProperty("client_id").GetString()!;
                            if (OversizedHandshake) { var bad = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(bad, 1); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(4), 1024 * 1024 + 1); await pipe.WriteAsync(bad, stop.Token).ConfigureAwait(false); break; }
                            await Send(pipe, 1, new { cmd = "DISPATCH", evt = "READY", data = new { v = 1 } }).ConfigureAwait(false);
                            await Send(pipe, 3, new { ping = "test" }).ConfigureAwait(false);
                        }
                        else if (opcode == 1)
                        {
                            if (RejectNext) { RejectNext = false; await Send(pipe, 1, new { cmd = "SET_ACTIVITY", evt = "ERROR", nonce = root.GetProperty("nonce").GetString(), data = new { code = 4000 } }).ConfigureAwait(false); }
                            else
                            {
                                var activity = root.GetProperty("args").GetProperty("activity").Clone(); Activities.Enqueue((activity, DateTimeOffset.UtcNow));
                                await Send(pipe, 1, new { cmd = "SET_ACTIVITY", nonce = root.GetProperty("nonce").GetString(), data = (object?)null }).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        private async Task Send(NamedPipeServerStream pipe, int opcode, object value)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(value); var frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length); body.CopyTo(frame, 8);
            await pipe.WriteAsync(frame.AsMemory(0, 3), stop.Token).ConfigureAwait(false);
            await pipe.WriteAsync(frame.AsMemory(3, 4), stop.Token).ConfigureAwait(false);
            await pipe.WriteAsync(frame.AsMemory(7), stop.Token).ConfigureAwait(false);
        }
        public void Dispose() { stop.Cancel(); Disconnect(); }
    }
}
