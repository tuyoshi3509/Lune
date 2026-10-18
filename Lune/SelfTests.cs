using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lune;
public static class SelfTests
{
    private static readonly List<string> Results = new();
    private static string Output => Path.Combine(Paths.Root, "test-output");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Results.Add("PASS: " + message); }
    private static void WriteResults(string name) { Directory.CreateDirectory(Output); File.WriteAllLines(Path.Combine(Output, name), Results); }
    public static void Capture(FrameworkElement window, string name)
    {
        Directory.CreateDirectory(Output); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(Output, name)); encoder.Save(output);
    }
    public static async Task Run(MainWindow main)
    {
        try
        {
            main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Library.ExtraFiles.Clear();
            var fixtures = Path.Combine(Paths.Data, "fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtures);
            var a = Path.Combine(fixtures, "日本語 空白 100% #一曲.wav"); var b = Path.Combine(fixtures, "second.wav"); var c = Path.Combine(fixtures, "third.wav");
            foreach (var path in new[] { a, b, c }) CreateWave(path, 3);
            using (var f = TagLib.File.Create(a)) { f.Tag.Title = "テストの曲"; f.Tag.Performers = new[] { "Artist One" }; f.Tag.Album = "Moonlight"; f.Tag.Track = 2; f.Save(); }
            using (var f = TagLib.File.Create(b)) { f.Tag.Title = "Sunset"; f.Tag.Performers = new[] { "Artist Two" }; f.Tag.Album = "テスト"; f.Tag.Track = 1; f.Save(); }
            main.Preferences.Folders = new List<string> { fixtures }; main.Preferences.MediaKeys = false;
            await main.ScanLibraryAsync();
            Check(main.Library.Tracks.Count == 3, "scan audio files including Japanese, spaces, percent and hash paths");
            var first = main.Library.Tracks.Single(t => t.Path == a); var second = main.Library.Tracks.Single(t => t.Path == b); var third = main.Library.Tracks.Single(t => t.Path == c);
            Check(first.Title == "テストの曲" && first.Number == 2 && first.Seconds >= 2.9, "read tags and duration from real WAV file");
            Check(SearchService.Filter(main.Library.Tracks, "テスト", 1).Single() == first, "title-only search excludes album matches");
            Check(SearchService.Filter(main.Library.Tracks, "テスト", 2).Single() == second, "album-only search excludes title matches");
            Check(SearchService.Filter(main.Library.Tracks, "artist one", 3).Single() == first, "case-insensitive artist-only search");
            Check(SearchService.Filter(main.Library.Tracks, "テスト", 0).Count() == 2, "all-fields search");
            Check(SearchService.Filter(main.Library.Tracks, "artist moon", 0).Single() == first, "multiple terms across fields");
            Check(SearchService.Filter(main.Library.Tracks, "ﾃｽﾄ", 1).Single() == first, "half-width Japanese search");
            var clock = Stopwatch.StartNew(); for (var i = 0; i < 30; i++) _ = SearchService.Filter(Enumerable.Repeat(main.Library.Tracks, 2000).SelectMany(t => t), "テスト", 0).Count();
            Results.Add($"Search benchmark, 6,000 tracks × 30: {clock.ElapsedMilliseconds} ms");
            main.ToggleFavorite(first); await main.ScanLibraryAsync(); first = main.Library.Tracks.Single(t => t.Path == a);
            Check(first.Favorite, "favorites survive incremental rescan");
            await main.AddPathsAsync(new[] { a, b }); Check(main.Library.Tracks.Count == 3, "file additions deduplicate by canonical path");
            var playlist = new Playlist { Name = "テスト・マイリスト" }; main.Library.Playlists.Add(playlist); main.AddToPlaylist(playlist, new[] { first, second, third, first });
            Check(playlist.TrackIds.Count == 3, "playlist additions deduplicate");
            main.ReorderPlaylist(playlist, new List<Track> { third }, first.Id); Check(playlist.TrackIds.SequenceEqual(new[] { third.Id, first.Id, second.Id }), "drag reorder preserves other tracks");
            main.ReorderPlaylist(playlist, new List<Track> { third, first }, null); Check(playlist.TrackIds.SequenceEqual(new[] { second.Id, third.Id, first.Id }), "multi-selection reorder to end");
            var xml = Path.Combine(fixtures, "Library.xml");
            File.WriteAllText(xml, $"<?xml version=\"1.0\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Tracks</key><dict><key>7</key><dict><key>Location</key><string>{System.Security.SecurityElement.Escape(new Uri(a).AbsoluteUri)}</string></dict><key>8</key><dict><key>Location</key><string>{System.Security.SecurityElement.Escape(new Uri(b).AbsoluteUri)}</string></dict></dict><key>Playlists</key><array><dict><key>Name</key><string>Imported list</string><key>Playlist Persistent ID</key><string>ABC</string><key>Playlist Items</key><array><dict><key>Track ID</key><integer>8</integer></dict><dict><key>Track ID</key><integer>7</integer></dict></array></dict></array></dict></plist>");
            var imported = LibraryService.ImportItunes(xml, main.Library); Check(imported.playlists == 1 && imported.tracks == 0, "iTunes XML maps encoded local URLs without copying");
            Check(main.Library.Playlists.Last().TrackIds.SequenceEqual(new[] { second.Id, first.Id }), "iTunes playlist order");
            LibraryService.ImportItunes(xml, main.Library); Check(main.Library.Playlists.Count == 2, "reimport iTunes XML updates same source playlist");
            var m3u = Path.Combine(fixtures, "Mix.m3u8"); File.WriteAllLines(m3u, new[] { "#EXTM3U", Path.GetFileName(a), Path.GetFileName(b), Path.GetFileName(a), "https://invalid.example/audio.mp3" });
            Check(LibraryService.ImportM3u(m3u, main.Library) == 2, "M3U relative paths, Unicode, deduplication, ignores remote entries");
            var store = new Store(); store.Save("test-state.json", main.Library); var roundtrip = store.Load<LibraryState>("test-state.json");
            Check(roundtrip.Tracks.Count == 3 && roundtrip.Playlists.Count == 3 && roundtrip.Tracks.Any(t => t.Favorite), "library, favorites and playlists persist");
            store.Save("test-state.json", main.Library); File.WriteAllText(Path.Combine(Paths.Data, "test-state.json"), "broken");
            Check(store.Load<LibraryState>("test-state.json").Tracks.Count == 3, "atomic store recovers backup after corruption");
            var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await LibraryService.ScanAsync(main.Preferences, main.Library, new Progress<string>(), canceled.Token); Check(false, "scan cancellation"); } catch (OperationCanceledException) { Check(main.Library.Tracks.Count == 3, "cancelled scan preserves old library"); }
            Check(DownloadService.ValidateUrl("https://youtu.be/test") == "https://youtu.be/test", "YouTube short URL accepted");
            foreach (var invalid in new[] { "file:///C:/test", "https://youtube.com.evil.example/test", "https://user@youtube.com/test", "http://youtube.com/test" })
            {
                try { DownloadService.ValidateUrl(invalid); Check(false, "URL validation"); } catch (ArgumentException) { Results.Add("PASS: reject invalid URL " + invalid); }
            }
            Check(DownloadService.SafeName("CON") != "CON" && !DownloadService.SafeName("a/b:c*").Contains('/'), "safe filenames handle reserved Windows names and invalid characters");
            main.P.Muted = true; main.P.Volume = 0.2; main.P.Shuffle = false; main.P.Repeat = 0;
            main.P.SetQueue(new[] { first, second, third }, first, true);
            await WaitFor(() => main.P.IsPlaying && main.P.Position > 0, "WAV playback opens and progresses");
            Check(main.P.Duration >= 2.9, "decoder duration available");
            main.P.Toggle(); var paused = main.P.Position; await Task.Delay(400); Check(!main.P.IsPlaying && Math.Abs(main.P.Position - paused) < 0.1, "pause halts playback clock");
            main.P.Seek(1.2); Check(Math.Abs(main.P.Position - 1.2) < 0.1, "seek while paused");
            main.P.Toggle(); await WaitFor(() => main.P.IsPlaying, "resume playback");
            main.P.Next(); await WaitFor(() => main.P.Current == second && main.P.IsPlaying, "next track");
            main.P.Previous(); await WaitFor(() => main.P.Current == first && main.P.IsPlaying, "previous history");
            main.P.Stop(); Check(!main.P.IsPlaying && main.P.Position == 0, "stop resets position");
            main.P.SetQueue(new[] { first, second, third }, first, false); main.P.Shuffle = true;
            var seen = new HashSet<string> { main.P.Current!.Id }; main.P.Next(); seen.Add(main.P.Current!.Id); main.P.Next(); seen.Add(main.P.Current!.Id);
            Check(seen.Count == 3, "shuffle visits all tracks before repeating"); main.P.Next(true); Check(!main.P.IsPlaying && main.P.Position == 0, "shuffle stops at end with repeat off");
            main.P.Shuffle = false; main.P.Repeat = 1; main.P.SetQueue(new[] { first, second }, second, false); main.P.Next(true);
            Check(main.P.Current == first, "repeat all wraps queue");
            main.P.Repeat = 0; main.P.Stop(); main.P.SetQueue(new[] { first, second }, first, false); main.P.Enqueue(new[] { third }, true);
            Check(main.P.Queue.Select(t => t.Id).SequenceEqual(new[] { first.Id, third.Id, second.Id }), "play next inserts after current track");
            main.P.RemoveAt(1); Check(main.P.Current == first && main.P.Queue.Count == 2, "remove queued track retains current");
            main.P.Volume = 5; Check(main.P.Volume == main.P.VolumeMaximum, "volume clamped to configured maximum"); main.P.Volume = 0.2;
            main.Preferences.Opacity = 0.7; main.Preferences.Accent = "#99CFFF"; main.ApplyAppearance(); Check(main.Opacity == 0.7, "appearance opacity applies"); main.Preferences.Opacity = 1; main.ApplyAppearance();
            main.Navigate("tracks"); main.UpdateLayout(); Check(main.TrackGrid.Items.Count == 3, "track grid populated");
            main.SearchBox.Text = "Sunset"; await Task.Delay(300); Check(main.TrackGrid.Items.Count == 1, "debounced search updates UI"); main.SearchBox.Clear(); await Task.Delay(300);
            main.P.Stop(); main.SaveSettings(); Check(new Store().Load<Settings>("settings.json").Queue.Count == 2, "playback queue persists");
            var draggedPath = Path.Combine(fixtures, "dragged.wav"); CreateWave(draggedPath, 3);
            var external = new DataObject(DataFormats.FileDrop, new[] { draggedPath });
            main.RaiseEvent(DropArgs(external, main, UIElement.DropEvent));
            await WaitFor(() => main.Library.Tracks.Count == 4, "external file Drop event adds track through window handler");
            var dragged = main.Library.Tracks.Single(t => t.Path == draggedPath);
            var dropList = new Playlist { Name = "Drop target" }; main.Library.Playlists.Add(dropList);
            await main.AddPathsAsync(new[] { a }); main.PlaylistList.ScrollIntoView(dropList); main.UpdateLayout();
            var item = (ListBoxItem)main.PlaylistList.ItemContainerGenerator.ContainerFromItem(dropList);
            var internalDrop = new DataObject(); internalDrop.SetData("LuneTracks", new List<Track> { first, second });
            item.RaiseEvent(DropArgs(internalDrop, item, UIElement.PreviewDropEvent));
            Check(dropList.TrackIds.SequenceEqual(new[] { first.Id, second.Id }), "internal track Drop event adds to sidebar playlist");
            item.RaiseEvent(DropArgs(external, item, UIElement.PreviewDropEvent));
            await WaitFor(() => dropList.TrackIds.Contains(dragged.Id), "external file Drop event adds directly to sidebar playlist");
            main.PlaylistList.SelectedItem = dropList; main.UpdateLayout();
            typeof(MainWindow).GetField("dragPlaylistSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, dropList.Id);
            var row = (DataGridRow)main.TrackGrid.ItemContainerGenerator.ContainerFromItem(first);
            var reorder = new DataObject(); reorder.SetData("LuneTracks", new List<Track> { dragged });
            row.RaiseEvent(DropArgs(reorder, row, UIElement.DropEvent));
            Check(dropList.TrackIds.SequenceEqual(new[] { dragged.Id, first.Id, second.Id }), "playlist row Drop event changes saved playback order");
            typeof(MainWindow).GetField("dragPlaylistSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(main, null);
            main.P.Repeat = 2; main.P.SetQueue(new[] { first }, first, true); await WaitFor(() => main.P.IsPlaying, "repeat-one media opens");
            main.P.Seek(2.8); await Task.Delay(1000); Check(main.P.IsPlaying && main.P.Position < 2, "repeat one loops after actual MediaEnded"); main.P.Stop(); main.P.Repeat = 0;
            third.ReadIssue = "test unreadable metadata"; main.P.SetQueue(new[] { first, second, third }, play: false);
            Check(main.P.Queue.Count == 2, "continuous queue skips tracks with read issues");
            main.P.SetQueue(new[] { third }, third, false); Check(main.P.Current == third, "explicit selection can attempt flagged track"); third.ReadIssue = "";
            main.P.SetQueue(new[] { first, second }, first, false); main.P.Shuffle = true; main.P.Enqueue(new[] { dragged }, true); main.P.Next();
            Check(main.P.Current == dragged, "explicit play-next takes priority over shuffle"); main.P.Stop(); main.P.Shuffle = false;
            Capture(main, "self-test-tracks.png");
            await CheckListeningAndSorting(main, first, second, fixtures);
            await CheckVolumeAndSourceUrl(main, fixtures);
            Results.AddRange(await LibraryUiChecks.Run(main, fixtures));
            WriteResults("self-tests.txt"); main.ExitApp();
        }
        catch (Exception ex) { Results.Add(ex.ToString()); WriteResults("self-tests.txt"); Application.Current.Shutdown(1); }
    }
    private static async Task WaitFor(Func<bool> predicate, string message)
    {
        var watch = Stopwatch.StartNew(); while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(100); Check(predicate(), message);
    }
    private static async Task CheckVolumeAndSourceUrl(MainWindow main, string fixtures)
    {
        main.P.Stop(); main.P.Muted = true;
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"Volume\":0.9}")!;
        Check(legacy.VolumeMinimum == 0 && legacy.VolumeMaximum == .3, "old settings receive default volume range 0 to 30 percent");
        main.P.SetVolumeRange(0, .3); main.P.Volume = legacy.Volume;
        Check(main.P.Volume == .3 && main.VolumeSlider.Maximum == .3, "restored volume above limit is clamped to actual 30 percent");
        var settings = new SettingsWindow(main); settings.Show();
        settings.VolumeMinInput.Text = "10"; settings.VolumeMaxInput.Text = "20";
        settings.VolumeMaxInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); main.UpdateLayout();
        Check(main.P.VolumeMinimum == .1 && main.P.VolumeMaximum == .2 && main.P.Volume == .2, "settings inputs apply both bounds and clamp current volume");
        main.VolumeSlider.Value = .15;
        Check(Math.Abs(main.P.Volume - .15) < .0001, "main slider controls volume inside configured range");
        main.P.Volume = -1; Check(main.P.Volume == .1, "volume cannot drop below configured minimum");
        main.P.Volume = 2; Check(main.P.Volume == .2, "volume cannot exceed configured maximum");
        main.P.Volume = double.NaN; Check(main.P.Volume == .2, "non-finite volume is ignored");
        settings.VolumeMinInput.Text = "30"; settings.VolumeMaxInput.Text = "20";
        settings.VolumeMaxInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Check(main.P.VolumeMinimum == .1 && main.P.VolumeMaximum == .2, "inverted range is rejected without changing playback settings");
        settings.VolumeMinInput.Text = "NaN"; settings.VolumeMaxInput.Text = "Infinity";
        settings.VolumeMaxInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Check(main.P.VolumeMinimum == .1 && main.P.VolumeMaximum == .2, "non-finite range is rejected");
        settings.VolumeMinInput.Text = "0"; settings.VolumeMaxInput.Text = "30";
        settings.VolumeMaxInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Capture(settings, "lune-volume-settings.png"); settings.Close();
        main.ShowMini(); var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
        main.P.SetVolumeRange(.5, .8); main.UpdateLayout(); mini.UpdateLayout();
        Check(main.P.Volume == .5 && main.VolumeSlider.Value == .5 && mini.VolumeSlider.Value == .5 && mini.VolumeSlider.Maximum == .8, "shifting range above old maximum keeps both sliders and engine synchronized");
        mini.VolumeSlider.Value = .65;
        main.P.SetVolumeRange(.1, .7); main.UpdateLayout(); mini.UpdateLayout();
        Check(Math.Abs(main.P.Volume - .65) < .0001 && Math.Abs(main.VolumeSlider.Value - .65) < .0001, "range expansion preserves in-range volume with both sliders bound");
        main.P.SetVolumeRange(.1, .2); main.P.Volume = .2; main.P.Volume += main.P.VolumeStep;
        Check(main.P.Volume == .2 && main.P.VolumeStep < .01, "keyboard-sized changes respect range limit and narrow-range precision");
        main.SaveSettings(); var saved = new Store().Load<Settings>("settings.json");
        Check(saved.VolumeMinimum == .1 && saved.VolumeMaximum == .2 && saved.Volume == .2, "volume range and current value persist together");
        main.P.SetVolumeRange(0, .3); main.P.Volume = .15; main.RestoreMain();
        const string url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        foreach (var extension in new[] { "m4a", "mp3" })
        {
            var path = Path.Combine(fixtures, "source-url." + extension);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await DownloadService.Run(Path.Combine(Paths.Tools, "ffmpeg.exe"), new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "0.3", path }, null, timeout.Token);
            DownloadService.WriteImportTags(path, "URL保存テスト", "Lune", "元動画情報", "", url);
            var track = LibraryService.ReadTrack(path);
            Check(track.SourceUrl == url && track.Title == "URL保存テスト", extension + " embeds and reads original YouTube URL with track metadata");
            new Store().Save("source-url-test.json", new LibraryState { Tracks = new() { track } });
            Check(new Store().Load<LibraryState>("source-url-test.json").Tracks[0].SourceUrl == url, extension + " source URL survives library persistence");
            var details = new TrackDetailsWindow(main, track); details.Show(); details.UpdateLayout();
            Check(Visuals<TextBox>(details).Any(t => t.Text == url && t.IsReadOnly), extension + " source URL is visible in track details");
            if (extension == "m4a") Capture(details, "lune-source-url-details.png"); details.Close();
        }
    }
    public static async Task ExitOnClose(MainWindow main)
    {
        var args = Environment.GetCommandLineArgs(); var mode = args[Array.IndexOf(args, "--exit-test") + 1];
        main.P.Muted = true;
        var track = main.Library.Tracks.FirstOrDefault(t => t.ReadIssue.Length == 0 && File.Exists(t.Path));
        if (track != null) main.P.SetQueue(new[] { track });
        if (mode == "import")
        {
            var importer = new ImportWindow(main); importer.Show();
            Func<CancellationToken, Task> action = async ct => { await DownloadService.Run(Path.Combine(Paths.Tools, "ffmpeg.exe"), new[] { "-hide_banner", "-re", "-f", "lavfi", "-i", "anullsrc", "-t", "120", "-f", "null", "-" }, null, ct); };
            typeof(ImportWindow).GetMethod("Run", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(importer, new object[] { action });
            await WaitFor(() => DownloadService.ActiveProcessIds.Length > 0, "conversion helper is running before close");
        }
        File.WriteAllLines(Path.Combine(Paths.Data, "close-helper-pids.txt"), DownloadService.ActiveProcessIds.Select(id => id.ToString()));
        if (mode == "mini") { main.ShowMini(); Application.Current.Windows.OfType<MiniWindow>().Single().Close(); }
        else main.Close();
    }
    private static async Task CheckListeningAndSorting(MainWindow main, Track first, Track second, string fixtures)
    {
        main.P.Stop(); main.P.Repeat = 0; main.P.Shuffle = false; main.P.Muted = true;
        var longPath = Path.Combine(fixtures, "listening-time.wav"); CreateWave(longPath, 20);
        var track = LibraryService.ReadTrack(longPath); var stat = main.P.Listening.Get(track);
        Check(stat.TotalSeconds == 0 && track.AddedUtcTicks > 0 && !track.AddedDateEstimated, "new tracks start with zero listening time and actual import timestamp");
        main.P.SetQueue(new[] { track }); await WaitFor(() => stat.TotalSeconds > .2, "rendered audio accumulates listening time");
        main.P.Toggle(); var paused = stat.TotalSeconds; await Task.Delay(350);
        Check(Math.Abs(stat.TotalSeconds - paused) < .001, "paused time is excluded from listening history");
        main.P.Seek(12); Check(Math.Abs(stat.TotalSeconds - paused) < .001, "seek distance while paused is not counted");
        main.P.Toggle(); await Task.Delay(650); main.P.Seek(3); await Task.Delay(650); main.P.Stop();
        Check(stat.TotalSeconds - paused > .5 && stat.TotalSeconds - paused < 2.2, "forward and backward seeks count only actual playback segments");
        Check(Math.Abs(stat.EquivalentPlays!.Value - stat.TotalSeconds / 20) < .0001, "fractional play count is derived from cumulative time and decoded length");
        var stored = new ListeningHistory().Get(track);
        Check(Math.Abs(stored.TotalSeconds - stat.TotalSeconds) < .0001, "stopping persists fractional listening seconds");
        var beforeDuration = stat.TotalSeconds;
        main.P.SetQueue(new[] { track, second }, track); await Task.Delay(600); main.P.Next(); var afterSwitch = stat.TotalSeconds;
        await Task.Delay(500); main.P.Stop();
        Check(afterSwitch > beforeDuration && Math.Abs(stat.TotalSeconds - afterSwitch) < .001, "switching tracks credits the old track without charging it for the next track");
        var firstStats = main.P.Listening.Get(first); var fullStart = firstStats.TotalSeconds;
        main.P.SetQueue(new[] { first }); await WaitFor(() => !main.P.IsPlaying, "full track reaches automatic stop");
        Check(Math.Abs(firstStats.TotalSeconds - fullStart - 3) < .05, "natural end credits complete track including final timer interval");
        var repeatStart = firstStats.TotalSeconds;
        main.P.Repeat = 2; main.P.SetQueue(new[] { first }, first, true, 2.5);
        await WaitFor(() => firstStats.TotalSeconds - repeatStart > .8, "repeat credits subsequent playback segment"); main.P.Stop(); main.P.Repeat = 0;
        Check(firstStats.TotalSeconds - repeatStart < 2, "repeat after seeking does not charge skipped beginning");
        main.P.SetQueue(new[] { second }, play: false);
        var added = first.AddedUtcTicks;
        using (var file = TagLib.File.Create(first.Path)) { file.Tag.Comment = "metadata refresh fixture"; file.Save(); }
        var reread = LibraryService.ReadTrack(first.Path, first);
        Check(reread.AddedUtcTicks == added && ReferenceEquals(main.P.Listening.Get(reread), firstStats), "metadata refresh retains original addition time and listening record");
        var legacy = new Track { Id = "legacy", Path = first.Path, Modified = first.Modified };
        var estimated = await LibraryOrdering.EstimateExistingDatesAsync(new[] { legacy });
        Check(estimated[legacy.Id] == File.GetCreationTimeUtc(first.Path).Ticks, "existing tracks use file creation date for initial addition estimate");
        Check(new ListeningStats { TotalSeconds = 90061 }.TotalTimeText.StartsWith("25時間"), "cumulative time does not wrap after 24 hours");
        Check(new ListeningStats().EquivalentPlays == null, "unknown duration does not divide by zero");
        var details = new TrackDetailsWindow(main, first); details.Show(); await Task.Delay(100); details.UpdateLayout();
        Check(Visuals<TextBlock>(details).Any(t => t.Text == firstStats.PlayCountText), "track details shows cumulative-time play count");
        main.P.SetQueue(new[] { first }, first, true); await Task.Delay(650); main.P.Toggle(); details.UpdateLayout();
        Check(Visuals<TextBlock>(details).Any(t => t.Text == firstStats.TotalSecondsText), "open details updates when listening time changes");
        Capture(details, "lune-track-details.png"); details.Close(); main.P.Stop();
        var original = main.Library.Tracks;
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new Track { Id = "sort-a", Title = "Cedar", Artist = "Artist", Album = "Zulu", AddedUtcTicks = date.AddDays(1).Ticks };
        var b = new Track { Id = "sort-b", Title = "Amber", Artist = "Artist", Album = "Amber", AddedUtcTicks = date.AddDays(2).Ticks };
        var c = new Track { Id = "sort-c", Title = "Birch", Artist = "Artist", Album = "Zulu", AddedUtcTicks = date.Ticks };
        main.Library.Tracks = new List<Track> { a, b, c };
        try
        {
            main.Navigate("tracks");
            foreach (var (mode, expected) in new[] { ("nameAsc", new[] { b, c, a }), ("nameDesc", new[] { a, c, b }), ("addedAsc", new[] { c, a, b }), ("addedDesc", new[] { b, a, c }) })
            {
                main.SortSelector.SelectedValue = mode; main.UpdateLayout();
                Check(main.TrackGrid.Items.Cast<Track>().SequenceEqual(expected), "song sorting UI: " + mode);
            }
            main.Navigate("albums");
            foreach (var (mode, expected) in new[] { ("nameAsc", new[] { "Amber", "Zulu" }), ("nameDesc", new[] { "Zulu", "Amber" }), ("addedAsc", new[] { "Zulu", "Amber" }), ("addedDesc", new[] { "Amber", "Zulu" }) })
            {
                main.SortSelector.SelectedValue = mode; main.UpdateLayout();
                Check(main.AlbumItems.Items.Cast<AlbumCard[]>().SelectMany(row => row).Select(card => card.Title).SequenceEqual(expected), "album sorting UI: " + mode);
            }
            main.SaveSettings(); var prefs = new Store().Load<Settings>("settings.json");
            Check(prefs.TrackSort == "addedDesc" && prefs.AlbumSort == "addedDesc", "song and album sort preferences persist separately");
            main.SearchBox.Text = "Zulu"; await Task.Delay(250);
            Check(main.AlbumItems.Items.Cast<AlbumCard[]>().SelectMany(row => row).Single().Title == "Zulu", "search retains selected sorting mode");
        }
        finally { main.Library.Tracks = original; main.Preferences.TrackSort = "default"; main.Preferences.AlbumSort = "nameAsc"; main.SearchBox.Clear(); main.Navigate("tracks"); }
    }
    private static DragEventArgs DropArgs(IDataObject data, DependencyObject target, RoutedEvent routed)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, target, new Point(10, 10) }, System.Globalization.CultureInfo.InvariantCulture)!;
        args.RoutedEvent = routed; return args;
    }
    public static async Task Smoke(MainWindow main)
    {
        try
        {
            Check(main.Library.Tracks.Count > 5000, "real library loaded");
            Check(main.Library.Tracks.Any(t => t.Artwork.Length > 0 && File.Exists(t.Artwork)), "real album artwork extracted");
            Results.Add($"Library: {main.Library.Tracks.Count} tracks; {main.Library.Tracks.Select(t => t.AlbumKey).Distinct().Count()} albums; {main.Library.Tracks.Count(t => t.Artwork.Length > 0)} tracks with artwork; {main.Library.ProtectedFiles} protected files; {main.Library.ReadErrors} read errors");
            await CheckDevicesAndGallery(main);
            main.P.Muted = true;
            foreach (var extension in new[] { ".m4a", ".mp3", ".flac" })
            {
                var t = main.Library.Tracks.First(t => Path.GetExtension(t.Path) == extension && t.ReadIssue.Length == 0 && t.Modified > 0);
                var modified = File.GetLastWriteTimeUtc(t.Path); var size = new FileInfo(t.Path).Length;
                main.P.SetQueue(new[] { t }); await WaitFor(() => main.P.IsPlaying && main.P.Position > 0, extension + " plays on this PC"); main.P.Stop();
                Check(modified == File.GetLastWriteTimeUtc(t.Path) && size == new FileInfo(t.Path).Length, extension + " source file unchanged");
            }
            main.P.SetQueue(main.Library.Tracks.Where(t => t.Artwork.Length > 0 && t.ReadIssue.Length == 0).Take(8), play: false);
            main.P.Muted = false; main.Navigate("albums"); await Task.Delay(400); Capture(main, "lune-library.png");
            main.Navigate("tracks"); await Task.Delay(200); Capture(main, "lune-tracks.png");
            main.Navigate("artists"); await Task.Delay(200); Capture(main, "lune-artists.png");
            main.Navigate("albums"); main.UpdateLayout(); var container = FindVisual<Button>(main.AlbumItems); container?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(main.PageTitle.Text != "アルバム", "album card click opens track detail");
            await Task.Delay(200); Capture(main, "lune-album-detail.png");
            var settings = new SettingsWindow(main); settings.Show(); await Task.Delay(150); settings.UpdateLayout();
            Check(Visuals<TextBlock>(settings.DeviceSelector).Any(t => t.Text == "Windowsの既定のデバイス"), "device selector renders friendly name instead of object details");
            Capture(settings, "lune-settings.png"); settings.Close();
            var import = new ImportWindow(main); import.Show(); await Task.Delay(150); Capture(import, "lune-import.png"); import.Close();
            using var proc = Process.GetCurrentProcess(); proc.Refresh(); Results.Add($"Working set after browsing: {proc.WorkingSet64 / 1048576d:F1} MiB; private: {proc.PrivateMemorySize64 / 1048576d:F1} MiB");
            main.ShowMini(); await Task.Delay(200); var mini = Application.Current.Windows.OfType<MiniWindow>().Single(); Capture(mini, "lune-mini.png");
            await Task.Delay(2500);
            var cpu = proc.TotalProcessorTime; var wall = Stopwatch.StartNew(); await Task.Delay(5000); proc.Refresh();
            Results.Add($"Mini paused CPU over {wall.Elapsed.TotalSeconds:F1}s: {(proc.TotalProcessorTime - cpu).TotalMilliseconds:F1} ms; working set {proc.WorkingSet64 / 1048576d:F1} MiB");
            Check(!main.P.IsPlaying, "mini is paused after restore");
            main.P.Stop(); main.Navigate("albums"); main.RestoreMain(); WriteResults("smoke-tests.txt"); main.ExitApp();
        }
        catch (Exception ex) { Results.Add(ex.ToString()); WriteResults("smoke-tests.txt"); Application.Current.Shutdown(1); }
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) return t; var found = FindVisual<T>(child); if (found != null) return found; } return null;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
    private static async Task CheckDevicesAndGallery(MainWindow main)
    {
        main.P.Muted = true; main.P.Volume = .23; main.P.Shuffle = false; main.P.Repeat = 0;
        var expectedVolume = main.P.Volume;
        main.P.RefreshOutputDevices();
        var devices = main.P.OutputDevices.Where(d => d.Id.Length > 0 && !d.IsRecording && d.CanSelect && d.Group == "再生デバイス").ToList();
        Results.Add("Output devices: " + string.Join(" | ", devices.Select(d => d.Name)));
        Check(devices.Count > 0, "Windows playback endpoints are enumerated");
        var path = Path.Combine(Paths.Data, "device-switch.wav"); CreateWave(path, 20);
        var track = new Track { Id = "device-test", Path = path, Title = "Device test", Seconds = 20 };
        main.P.SetQueue(new[] { track }, play: false);
        var settings = new SettingsWindow(main); settings.Show();
        settings.DeviceSelector.SelectedValue = devices[0].Id;
        Check(main.P.OutputDeviceId == devices[0].Id && !main.P.IsPlaying, "settings selector changes output without starting paused audio");
        main.P.Toggle(); await WaitFor(() => main.P.IsPlaying && main.P.Position > .1, "selected WASAPI endpoint plays audio");
        main.P.Seek(4.25);
        settings.DeviceSelector.SelectedValue = devices.Count > 1 ? devices[1].Id : "";
        Check(main.P.IsPlaying && Math.Abs(main.P.Position - 4.25) < .5 && main.P.Current == track, "device switch preserves current track, playing state and position");
        await WaitFor(() => main.P.Position > 4.7, "audio clock advances after switching playback device");
        Check(main.P.Muted && Math.Abs(main.P.Volume - expectedVolume) < .001, "device switch preserves mute and volume");
        main.P.Toggle(); main.P.Seek(6.25); settings.DeviceSelector.SelectedValue = devices[0].Id;
        await Task.Delay(300);
        Check(!main.P.IsPlaying && Math.Abs(main.P.Position - 6.25) < .05, "paused device switch retains position without autoplay");
        main.SaveSettings();
        Check(new Store().Load<Settings>("settings.json").OutputDeviceId == devices[0].Id, "selected endpoint ID persists to settings");
        using (var restored = new PlayerService())
        {
            restored.OutputDeviceId = new Store().Load<Settings>("settings.json").OutputDeviceId;
            Check(restored.ActiveOutputDeviceId == devices[0].Id, "saved endpoint resolves on a fresh player");
        }
        main.P.OutputDeviceId = "missing-device-for-test";
        Check(main.P.ActiveOutputDeviceId.Length > 0 && main.P.OutputDeviceId == "missing-device-for-test", "disconnected saved endpoint falls back while retaining preference");
        main.P.Toggle(); await WaitFor(() => main.P.IsPlaying && main.P.Position > 6.5, "fallback endpoint plays from saved position");
        main.P.Stop(); main.P.OutputDeviceId = ""; settings.Close();
        main.Navigate("albums"); main.UpdateLayout(); await Task.Delay(200);
        var total = main.Library.Tracks.Select(t => t.AlbumKey).Distinct().Count();
        var rows = main.AlbumItems.Items.Cast<AlbumCard[]>().ToList();
        Check(rows.Sum(r => r.Length) == total && total > 36, "gallery contains every album with no page limit");
        var panel = FindVisual<System.Windows.Controls.VirtualizingStackPanel>(main.AlbumItems)!;
        var scroll = FindVisual<ScrollViewer>(main.AlbumItems)!;
        Check(panel.Children.Count < rows.Count / 2 && scroll.ScrollableHeight > 0, "off-screen album rows are virtualized");
        var galleryBar = FindVisual<System.Windows.Controls.Primitives.ScrollBar>(main.AlbumItems)!;
        var galleryThumb = FindVisual<System.Windows.Controls.Primitives.Thumb>(galleryBar)!;
        var thumbBody = FindVisual<Border>(galleryThumb)!;
        Check(galleryThumb.ActualHeight >= 28 && thumbBody.ActualHeight >= 26 && thumbBody.ActualWidth == 6, "large library scrollbar has an unclipped vertical pill with minimum height");
        Capture(main, "lune-scrollbar-top.png");
        Results.Add($"Gallery: {total} albums / {rows.Count} rows, {panel.Children.Count} realized rows at top");
        scroll.ScrollToBottom(); main.UpdateLayout(); await Task.Delay(250); main.UpdateLayout();
        Check(main.AlbumItems.ItemContainerGenerator.ContainerFromIndex(rows.Count - 1) != null, "last album row is reachable by scrolling to bottom");
        Check(panel.Children.Count < rows.Count / 2, "gallery stays virtualized after scrolling to last album");
        Capture(main, "lune-gallery-bottom.png");
        var lastRow = (ListBoxItem)main.AlbumItems.ItemContainerGenerator.ContainerFromIndex(rows.Count - 1);
        var lastButton = FindVisual<Button>(lastRow)!;
        var card = (AlbumCard)lastButton.Tag;
        lastButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(main.TrackGrid.Items.Count == card.Tracks.Count && main.PageTitle.Text == card.Title, "album near end of collection opens its tracks");
        main.Navigate("albums"); main.Width = 980; main.UpdateLayout(); await Task.Delay(150);
        Check(main.AlbumItems.Items.Cast<AlbumCard[]>().Sum(r => r.Length) == total, "resizing recomposes rows without losing albums");
        Capture(main, "lune-gallery-narrow.png");
        main.Width = 1200; main.UpdateLayout();
        main.Navigate("artists"); main.UpdateLayout();
        scroll = FindVisual<ScrollViewer>(main.AlbumItems)!; scroll.ScrollToBottom(); main.UpdateLayout(); await Task.Delay(150);
        Check(main.AlbumItems.ItemContainerGenerator.ContainerFromIndex(main.AlbumItems.Items.Count - 1) != null, "all artists are reachable in the continuous gallery");
        main.Navigate("albums"); main.SearchField.SelectedIndex = 2; main.SearchBox.Text = card.Title; await Task.Delay(350);
        Check(main.AlbumItems.Items.Cast<AlbumCard[]>().SelectMany(r => r).Any(c => c.Title == card.Title), "search finds albums beyond former first page");
        main.SearchBox.Clear(); main.SearchField.SelectedIndex = 0; await Task.Delay(250);
    }
    public static async Task Perf(MainWindow main)
    {
        using var proc = Process.GetCurrentProcess();
        async Task Measure(string name)
        {
            await Task.Delay(3000); proc.Refresh(); var cpu = proc.TotalProcessorTime; var clock = Stopwatch.StartNew();
            await Task.Delay(6000); proc.Refresh();
            Results.Add($"{name}: CPU {(proc.TotalProcessorTime - cpu).TotalMilliseconds:F1}ms / {clock.Elapsed.TotalSeconds:F1}s; working set {proc.WorkingSet64 / 1048576d:F1}MiB; private {proc.PrivateMemorySize64 / 1048576d:F1}MiB; playing={main.P.IsPlaying}");
            WriteResults("performance.txt");
        }
        await Measure("Main paused"); main.ShowMini(); await Measure("Mini paused");
        main.P.Muted = true; main.P.SetQueue(main.Library.Tracks.Where(t => t.ReadIssue.Length == 0 && t.Modified > 0).Take(5));
        await Measure("Mini playback"); main.P.Stop(); main.P.Muted = false; main.ExitApp();
    }
    public static async Task Activation(MainWindow main)
    {
        try
        {
            async Task LaunchAgain()
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--data"); start.ArgumentList.Add(Paths.Data);
                using var second = Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await second.WaitForExitAsync(timeout.Token);
                Check(second.ExitCode == 0, "second launch exits without a duplicate-instance dialog");
                await WaitFor(() => main.IsVisible && main.WindowState != WindowState.Minimized, "second launch restores existing main window");
                Check(main.ShowInTaskbar, "restored window has a taskbar entry");
            }
            main.Hide(); await LaunchAgain();
            main.ShowMini(); await LaunchAgain();
            Check(!Application.Current.Windows.OfType<MiniWindow>().Any(w => w.IsVisible), "restoring from mini hides mini window");
            main.WindowState = WindowState.Minimized; await LaunchAgain();
            main.Left = -32000; main.Top = -32000; main.Hide(); await LaunchAgain();
            Check(main.Left > -32000 && main.Top > -32000, "off-screen window is moved back onto a monitor");
            WriteResults("activation-tests.txt"); main.ExitApp();
        }
        catch (Exception ex) { Results.Add(ex.ToString()); WriteResults("activation-tests.txt"); Application.Current.Shutdown(1); }
    }
    public static async Task<int> NetworkImport()
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var progress = new Progress<string>(s => File.AppendAllText(Path.Combine(Paths.Data, "network-progress.log"), s + Environment.NewLine));
            var info = await DownloadService.FetchAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ", cancellation.Token);
            Check(info.Title.Contains("Big Buck Bunny") && info.Seconds > 600, "YouTube metadata fetched through application service");
            var art = await DownloadService.FetchArtAsync(info.Thumbnail, cancellation.Token); Check(File.Exists(art), "thumbnail downloaded and resized");
            var target = Path.Combine(Paths.Data, "converted");
            foreach (var format in new[] { "m4a", "mp3" })
            {
                var path = await DownloadService.ImportAsync(info, "変換テスト / Big Buck Bunny", "Blender Foundation", "Lune の取り込み確認", format, art, target, progress, cancellation.Token);
                Check(File.Exists(path), format + " downloaded and converted");
                using var file = TagLib.File.Create(path);
                Check(file.Tag.Title == "変換テスト / Big Buck Bunny" && file.Tag.Album == "Lune の取り込み確認" && file.Tag.FirstPerformer == "Blender Foundation", format + " Japanese metadata embedded");
                Check(file.Tag.Pictures.Length > 0 && file.Properties.Duration.TotalSeconds > 600, format + " cover art and full duration verified");
            }
            File.WriteAllText(Path.Combine(target, "ATTRIBUTION.txt"), "Test audio derived from Big Buck Bunny, Blender Foundation.\nhttps://www.youtube.com/watch?v=aqz-KE-bpKQ\nCreative Commons Attribution license (as reported by YouTube).\nAudio extracted to M4A and MP3; metadata edited for testing.\n");
            using var cancel = new CancellationTokenSource(400);
            try { await DownloadService.Run(Path.Combine(Paths.Tools, "ffmpeg.exe"), new[] { "-hide_banner", "-re", "-f", "lavfi", "-i", "anullsrc", "-t", "30", "-f", "null", "-" }, null, cancel.Token); Check(false, "process cancellation"); }
            catch (OperationCanceledException) { Check(true, "conversion subprocess cancellation"); }
            WriteResults("network-import-tests.txt"); return 0;
        }
        catch (Exception ex) { Results.Add(ex.ToString()); WriteResults("network-import-tests.txt"); return 1; }
    }
    public static void CreateWave(string path, int seconds)
    {
        const int rate = 22050; var data = new byte[rate * seconds * 2];
        using var writer = new BinaryWriter(File.Create(path)); writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(data.Length); writer.Write(data);
    }
}
