using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Lune;

internal static class DistributionChecks
{
    internal static async Task Run(MainWindow main)
    {
        var lines = new List<string>();
        var output = Path.Combine(Paths.Root, "test-output"); Directory.CreateDirectory(output);
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); lines.Add("PASS: " + label); }
        try
        {
            Check(main.Library.Tracks.Count == 0 && main.Preferences.Folders.Count == 0, "fresh distribution has no personal tracks or folder paths");
            Check(main.IsVisible && !Application.Current.Windows.OfType<MiniWindow>().Any(w => w.IsVisible), "fresh launch displays only normal player");
            Check(!main.Preferences.DiscordEnabled && main.Preferences.SecondaryOutputDeviceId == AudioRoutes.Off, "external presence and second output disabled by default");
            SelfTests.Capture(main, "distribution-first-launch.png");
            var path = Path.Combine(Paths.Data, "distribution-silent.wav"); SelfTests.CreateWave(path, 30);
            await main.AddPathsAsync(new[] { path });
            var track = main.Library.Tracks.Single();
            main.P.Muted = true; main.PlayTrack(track); await Task.Delay(800);
            Check(main.P.IsPlaying && main.P.Position > 0, "bundled executable decodes and plays local WAV");
            Check(main.ToolsData.Recent.FirstOrDefault() == track.Id, "recent history records playback");
            main.P.Seek(12); await Task.Delay(650);
            Check(main.P.Position >= 12 && main.P.Position < 15, "seek persists during playback");
            main.P.Toggle(); main.P.Seek(3); main.P.SetLoopStart(); main.P.Seek(5); main.P.SetLoopEnd();
            Check(main.P.Position == 3, "A-B repeat returns to A");
            main.P.Toggle(); await Task.Delay(2600);
            Check(main.P.Position >= 3 && main.P.Position < 5.5, "A-B repeat wraps at B");
            main.P.ClearLoop(); main.P.SetSleepTimer(1);
            Check(main.P.UtilityStatus.Contains("停止予定"), "sleep timer arms");
            main.P.SetSleepTimer(0); Check(!main.P.UtilityStatus.Contains("停止予定"), "sleep timer cancels");
            main.P.Enqueue(new[] { track }, false); main.SaveQueueAsPlaylist("Distribution queue");
            Check(main.Library.Playlists.Single().TrackIds.SequenceEqual(new[] { track.Id }), "queue saves as playlist");
            main.ToolsData.Bookmarks.Add(new MusicBookmark { TrackId = track.Id, Name = "Position", Position = 8 }); main.SaveMusicTools();
            Check(new Store().Load<MusicToolsData>("music-tools.json").Bookmarks.Single().Position == 8, "bookmarks persist");
            main.P.StopAfterTrack = true; main.P.Seek(29.6); await Task.Delay(1000);
            Check(!main.P.IsPlaying && !main.P.StopAfterTrack, "stop-after-track overrides queued playback");
            main.ShowMini(); await Task.Delay(120);
            var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
            main.Preferences.MiniTopmost = true; mini.ApplyAppearance();
            Check(mini.IsVisible && mini.Topmost && mini.PinButton.IsChecked == true, "mini pin pressed means ON");
            mini.PinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!mini.Topmost && mini.PinButton.IsChecked == false, "mini pin click means OFF");
            SelfTests.Capture(mini, "distribution-mini.png");
            main.RestoreMain();
            var tools = new MusicToolsWindow(main); tools.Show(); tools.UpdateLayout();
            Check(tools.IsVisible, "convenience window opens"); SelfTests.Capture(tools, "distribution-tools.png"); tools.Close();
            File.WriteAllLines(Path.Combine(output, "distribution-tests.txt"), lines);
            main.Close();
        }
        catch (Exception ex) { lines.Add("FAIL: " + ex); File.WriteAllLines(Path.Combine(output, "distribution-tests.txt"), lines); Application.Current.Shutdown(1); }
    }
}
