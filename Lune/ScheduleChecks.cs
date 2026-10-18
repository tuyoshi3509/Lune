using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NAudio.Wave;

namespace Lune;

internal static class ScheduleChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var log = Path.Combine(Paths.Root, "test-output", "schedule-tests.txt");
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        async Task Wait(Func<bool> condition, string name)
        {
            var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 7) await Task.Delay(25);
            Check(condition(), name);
        }
        try
        {
            var legacy = JsonSerializer.Deserialize<Playlist>("{\"Id\":\"old\",\"Name\":\"既存\",\"TrackIds\":[\"a\"]}")!;
            Check(!legacy.ScheduleEnabled && legacy.TimeRanges.Count == 0 && legacy.ScheduleFadeSeconds == 5 && legacy.ScheduleLoop, "old playlists remain opt-out with five-second fade defaults");
            var playlist = new Playlist { Id = "hourly", Name = "一日のBGM", ScheduleEnabled = true, ScheduleFadeSeconds = 1, TrackIds = new() { "a", "b", "c" }, TimeRanges = new() {
                new() { StartHour = 6, EndHour = 8, TrackId = "a" }, new() { StartHour = 8, EndHour = 18, TrackId = "b" }, new() { StartHour = 22, EndHour = 6, TrackId = "c" } } };
            DateTime At(int hour, int minute = 0) => new(2026, 9, 27, hour, minute, 0);
            Check(PlaylistSchedule.At(playlist, At(7, 59))?.TrackId == "a" && PlaylistSchedule.At(playlist, At(8))?.TrackId == "b", "hourly ranges include start and exclude end");
            Check(PlaylistSchedule.At(playlist, At(23))?.TrackId == "c" && PlaylistSchedule.At(playlist, At(0))?.TrackId == "c" && PlaylistSchedule.At(playlist, At(6))?.TrackId == "a", "overnight range spans midnight and ends at exact hour");
            Check(PlaylistSchedule.At(playlist, At(19)) == null, "unassigned hours use normal playback");
            Check(PlaylistSchedule.Validate(playlist.TimeRanges, playlist.TrackIds) == null, "adjacent and overnight ranges validate");
            var overlap = playlist.TimeRanges.Select(r => r.Copy()).ToList(); overlap.Add(new() { StartHour = 5, EndHour = 7, TrackId = "a" });
            Check(PlaylistSchedule.Validate(overlap, playlist.TrackIds)?.Contains("重な") == true, "overlap across midnight is rejected");
            Check(PlaylistSchedule.Validate(new[] { new PlaylistTimeRange { StartHour = 0, EndHour = 24, TrackId = "a" } }, playlist.TrackIds) == null, "zero through twenty-four supports all day");
            Check(PlaylistSchedule.Validate(new[] { new PlaylistTimeRange { StartHour = 8, EndHour = 8, TrackId = "a" } }, playlist.TrackIds) != null, "ambiguous equal endpoints are rejected");
            Check(PlaylistSchedule.Validate(new[] { new PlaylistTimeRange { StartHour = 1, EndHour = 2, TrackId = "deleted" } }, playlist.TrackIds) != null, "missing playlist track cannot be scheduled");
            main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Library.ExtraFiles.Clear(); main.Preferences.Folders.Clear();
            var path = Path.Combine(Paths.Data, "schedule-silence.wav");
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2))) writer.Write(new byte[44100 * 4 * 30], 0, 44100 * 4 * 30);
            var tracks = new[] { new Track { Id = "a", Title = "朝のBGM", Artist = "Lune", Path = path, Seconds = 30 }, new Track { Id = "b", Title = "昼のBGM", Artist = "Lune", Path = path, Seconds = 30 }, new Track { Id = "c", Title = "夜のBGM", Artist = "Lune", Path = path, Seconds = 30 } };
            main.Library.Tracks.AddRange(tracks); main.Library.Playlists.Add(playlist);
            var player = main.P; player.Muted = true; player.Repeat = 0; player.Shuffle = false; player.SetVolumeRange(.1, .3); player.Volume = .2;
            player.SecondaryOutputDeviceId = AudioRoutes.Off;
            var clock = At(8); player.LocalNow = () => clock;
            main.PlaylistList.ItemsSource = main.Library.Playlists.ToList(); main.PlaylistList.SelectedItem = playlist;
            main.TogglePlayback();
            Check(player.Current == tracks[1] && player.IsPlaying && player.Queue.Count == 0 && player.IsScheduleClockRunning, "main play button starts current-hour BGM without filling manual queue");
            main.PlayPlaylist(playlist, tracks[2]); player.CheckSchedule();
            Check(player.Current == tracks[2] && !player.IsScheduleFading && player.IsScheduleClockRunning, "explicit song choice takes precedence until the next scheduled range");
            main.PlayPlaylist(playlist);
            clock = At(7, 59); player.CheckSchedule();
            await Task.Delay(220);
            Check(player.Current == tracks[1] && player.ScheduleGain is > 0 and < 1, "old track fades down gradually before switching, including clock moving backwards");
            player.Volume = .25;
            Check(player.ScheduleGain < 1 && player.Volume == .25 && player.VolumeMinimum == .1, "fade multiplier is independent of volume range and slider edits");
            await Wait(() => player.Current == tracks[0], "target switches after fade-out");
            Check(player.ScheduleGain < .3, "new track starts quietly for fade-in");
            await Wait(() => !player.IsScheduleFading, "fade-in completes");
            Check(player.ScheduleGain == 1 && player.Volume == .25 && player.Muted, "fade restores gain without changing user volume or mute");
            clock = At(8); // Exercise the actual one-second clock timer, not just a direct check.
            await Wait(() => player.Current == tracks[1] && !player.IsScheduleFading, "live timer notices boundary and completes transition");
            player.Seek(29.7);
            await Wait(() => player.Position < 2 && player.Current == tracks[1] && player.IsPlaying, "scheduled song loops at its natural end despite repeat being off");
            playlist.ScheduleLoop = false; player.Seek(29.7);
            await Wait(() => player.Current == tracks[2], "loop option off follows playlist order after track end");
            playlist.ScheduleLoop = true; clock = At(19); player.CheckSchedule();
            Check(player.Current == tracks[2] && !player.IsScheduleFading, "gap does not interrupt current track");
            clock = At(22); player.CheckSchedule();
            Check(!player.IsScheduleFading && player.Current == tracks[2], "matching next slot does not restart same track");
            player.Toggle(); var paused = player.Position; clock = At(6); player.CheckSchedule(); await Task.Delay(120);
            Check(!player.IsPlaying && !player.IsScheduleClockRunning && player.Current == tracks[2] && player.Position == paused, "pause stops clock work and never starts audio at a boundary");
            player.Toggle();
            Check(player.Current == tracks[0] && player.IsPlaying, "resume reselects the current hour");
            await Wait(() => !player.IsScheduleFading, "resumed song fades in");
            clock = At(8); player.CheckSchedule(); await Task.Delay(150); player.Stop(); await Task.Delay(1100);
            Check(!player.IsPlaying && player.Current == tracks[0] && !player.IsScheduleFading && player.ScheduleGain == 1, "stop cancels pending transition without delayed restart");
            player.Toggle(); await Wait(() => player.Current == tracks[1] && !player.IsScheduleFading, "play after stop respects current hour");
            clock = At(6); player.CheckSchedule(); await Task.Delay(120); player.PlayCollection(new[] { tracks[2] }); await Task.Delay(1100);
            Check(player.Current == tracks[2] && player.ScheduledPlaylistId == "" && !player.IsScheduleFading && player.ScheduleGain == 1, "manual album playback cancels previous schedule and fade");
            clock = At(8); main.PlayPlaylist(playlist); player.Enqueue(new[] { tracks[0] }, false); player.Seek(29.7);
            await Wait(() => player.Current == tracks[0] && player.IsQueuePlayback, "explicit queue still takes precedence at track end");
            Check(player.ScheduledPlaylistId == "" && player.Queue.Count == 1, "queue handoff leaves scheduled playback without adding scheduled tracks");
            player.RemoveAt(0); clock = At(7); main.PlayPlaylist(playlist);
            playlist.ScheduleEnabled = false; main.SaveLibrary();
            Check(player.ScheduledPlaylistId == "" && !player.IsScheduleClockRunning, "disabling schedule immediately stops clock processing");
            playlist.ScheduleEnabled = true; main.PlayPlaylist(playlist); player.Stop(); main.SaveSettings();
            Check(new Store().Load<Settings>("settings.json").LastScheduledPlaylistId == playlist.Id, "active playlist context is saved for later resume");
            player.PlayCollection(tracks, tracks[0], false); player.RestoreScheduledPlaylist(playlist, tracks); clock = At(8); player.Toggle();
            await Wait(() => player.Current == tracks[1] && !player.IsScheduleFading, "restored paused schedule checks actual time on resume");
            player.Stop();

            using (var engine = new AudioEngine { Volume = .2 })
            {
                var outputs = engine.OutputDevices.Where(d => d.Group == "再生デバイス" && d.Id.Length > 0).Take(2).ToArray();
                if (outputs.Length > 0) engine.SelectDevice(outputs[0].Id);
                if (outputs.Length > 1) engine.SelectSecondaryDevice(outputs[1].Id);
                engine.Open(new Uri(path)); engine.Play(); engine.TransitionGain = .25;
                Check(engine.StreamCount > 0 && engine.StreamGains.All(g => Math.Abs(g - .05) < .00001), "transition gain applies to every real output stream");
                engine.PrimaryMuted = true; Check(engine.StreamGains[0] == 0, "per-output mute remains effective during fade");
                engine.Volume = .3;
                if (outputs.Length > 1) Check(Math.Abs(engine.StreamGains[1] - .075) < .00001, "second output combines fade and user volume independently");
                engine.IsMuted = true; engine.TransitionGain = .8; Check(engine.StreamGains.All(g => g == 0), "global mute stays silent through fade changes");
            }

            var editor = new PlaylistScheduleWindow(main, playlist); editor.Show(); await Task.Delay(120);
            var boxes = Visuals<ComboBox>(editor).ToList();
            Check(boxes.Count >= 10 && boxes.Any(b => b.SelectedValue is "a"), "range editor renders selected hours and track controls");
            editor.Ranges.Add(new() { StartHour = 7, EndHour = 9, TrackId = "c" }); editor.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(editor.IsVisible && editor.ValidationText.Text.Contains("重な") && playlist.TimeRanges.Count == 3, "invalid overlapping edits leave original settings untouched");
            editor.Ranges.RemoveAt(3); editor.LoopCheck.IsChecked = false; editor.FadeInput.SelectedValue = 5;
            SelfTests.Capture(editor, "lune-schedule-settings.png");
            editor.SaveButton.BringIntoView(); await Task.Delay(90); SelfTests.Capture(editor, "lune-schedule-options.png");
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var saved = new Store().Load<LibraryState>("library.json").Playlists.Single(p => p.Id == playlist.Id);
            Check(!editor.IsVisible && saved.ScheduleEnabled && !saved.ScheduleLoop && saved.ScheduleFadeSeconds == 5 && saved.TimeRanges.Count == 3, "schedule and behavior options persist after saving editor");
            var cancelled = new PlaylistScheduleWindow(main, playlist); cancelled.Show(); cancelled.Ranges.Clear(); cancelled.Close();
            Check(playlist.TimeRanges.Count == 3, "cancelled editor does not alter stored ranges");
            main.PlaylistList.ItemsSource = main.Library.Playlists.ToList(); main.PlaylistList.SelectedItem = playlist; main.Width = 980; main.Height = 640; main.UpdateLayout(); await Task.Delay(100);
            Check(main.ScheduleButton.IsVisible && main.PageSubtitle.Text.Contains("時間帯再生 ON"), "playlist exposes schedule button and enabled indicator");
            SelfTests.Capture(main, "lune-schedule-playlist.png");
            File.WriteAllLines(log, results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(log, results); Application.Current.Shutdown(1); }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) yield return found; foreach (var nested in Visuals<T>(child)) yield return nested; }
    }
}
