using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lune;

internal static class EnhancementChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var output = Path.Combine(Paths.Root, "test-output");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        async Task Wait(Func<bool> predicate, string name) { var clock = Stopwatch.StartNew(); while (!predicate() && clock.Elapsed.TotalSeconds < 8) await Task.Delay(80); Check(predicate(), name); }
        try
        {
            Directory.CreateDirectory(output); main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Library.ExtraFiles.Clear(); main.Preferences.Folders.Clear();
            await MarqueeChecks.Run(main, Check);
            var fixtures = Path.Combine(Paths.Data, "fixtures"); Directory.CreateDirectory(fixtures);
            Track Make(string name, string album, uint number, uint disc = 1)
            {
                var path = Path.Combine(fixtures, name + ".wav"); CreateTone(path, 20, 750);
                return new Track { Id = LibraryService.TrackId(path), Path = path, Title = name, Artist = "集計テスト", Album = album, Number = number, Disc = disc, Seconds = 20, AddedUtcTicks = DateTime.UtcNow.Ticks };
            }
            var a1 = Make("長い曲名のスクロール確認用タイトル — Album A First Track — 表示領域を超えた部分もゆっくり読めます", "Album A", 1);
            var a2 = Make("A second", "Album A", 2); var a3 = Make("A disc two", "Album A", 1, 2);
            var b1 = Make("B first", "Album B", 1); var b2 = Make("B second", "Album B", 2);
            main.Library.Tracks.AddRange(new[] { a3, b2, a2, b1, a1 }); main.Navigate("tracks");
            main.P.Muted = true; main.P.Shuffle = false; main.P.Repeat = 0;
            await InteractionChecks.Run(main, new[] { a1, a2, a3 }, Check);
            await DiscArtworkChecks.Run(main, new[] { a1, a2, a3 }, Check);
            main.PlayTrack(a1); Check(main.P.Queue.Count == 0 && main.P.PlaybackOrder.SequenceEqual(new[] { a1, a2, a3 }), "album playback follows disc and track numbers without filling manual queue");
            main.P.Next(true); Check(main.P.Current == a2 && main.P.Queue.Count == 0, "album next track is automatic and absent from queue");
            main.P.Next(true); Check(main.P.Current == a3, "automatic order continues to next disc");
            main.PlayTrack(a1); main.P.Enqueue(new[] { b1 }, false);
            Check(main.P.Current == a1 && main.P.Queue.SequenceEqual(new[] { b1 }), "explicit enqueue keeps current song and adds only requested track");
            main.P.Next(true); Check(main.P.Current == b1 && main.P.Queue.SequenceEqual(new[] { b1 }), "explicit queue takes priority over automatic album next");
            main.P.Next(true); Check(main.P.Current == b2 && main.P.Queue.Count == 0, "after queued song finishes next track comes from its album without queue entries");
            main.PlayTrack(a1); main.P.Enqueue(new[] { a1 }, false); main.P.Next(true);
            Check(main.P.Current == a2 && main.P.Queue.Count == 0, "queue containing only current song still allows album automatic next");
            main.PlayTrack(a1); main.P.Seek(main.P.Duration - .25);
            await Wait(() => main.P.Current == a2 && main.P.IsPlaying, "actual track end advances album playback");
            main.P.Enqueue(new[] { b1 }, true); main.P.Seek(main.P.Duration - .25);
            await Wait(() => main.P.Current == b1 && main.P.IsPlaying, "actual track end honors play-next queue");
            main.SaveSettings(); var settings = new Store().Load<Settings>("settings.json");
            Check(settings.ExplicitQueue.SequenceEqual(new[] { b1.Id }) && settings.QueuePlayback, "manual queue persists separately from automatic playback order");
            using (var restored = new PlayerService()) { restored.Restore(new[] { b1 }, new[] { a1, a2, a3 }, a2, false, 4); Check(restored.Queue.Single() == b1 && restored.Current == a2 && !restored.IsPlaying && Math.Abs(restored.Position - 4) < .05, "restoring automatic playback preserves a separate pending queue"); }
            main.P.Seek(main.P.Duration - .25); await Wait(() => main.P.Current == b2 && main.P.Queue.Count == 0, "actual queue completion resumes album automatically"); main.P.Stop();
            var history = new ListeningHistory(); var stat1 = history.Get(a1); var stat2 = history.Get(b1);
            stat1.TrackSeconds = 120; stat1.TotalSeconds = 60; stat2.TrackSeconds = 240; stat2.TotalSeconds = 480;
            using (var aggregate = new ArtistListeningStats(new[] { a1, b1, a1 }, history))
            {
                Check(aggregate.Rows.Count == 2 && aggregate.TotalSeconds == 540 && aggregate.EquivalentPlays == 2.5, "artist total sums each track ratio instead of dividing combined durations");
                stat1.TotalSeconds += 60; Check(aggregate.TotalSeconds == 600 && aggregate.EquivalentPlays == 3, "artist aggregate updates while listening data changes");
                stat2.TrackSeconds = 0; Check(aggregate.TotalSeconds == 600 && aggregate.EquivalentPlays == 1 && aggregate.UnknownLengths == 1, "unknown durations retain listening time and are excluded from play count");
            }
            main.P.Listening.Add(a1, 60, 120); main.P.Listening.Add(b1, 480, 240);
            var details = new ArtistDetailsWindow(main, "集計テスト", main.Library.Tracks); details.Show(); await Task.Delay(100); SelfTests.Capture(details, "lune-artist-details.png");
            Check(Visuals<TextBlock>(details).Any(t => t.Text == details.Stats.PlayCountText), "artist details renders total play count and per-song table"); details.Close();
            Check(AppearanceThemes.All.Length == 20 && AppearanceThemes.All.Select(t => t.Id).Distinct().Count() == 20, "twenty distinct appearance presets exist");
            foreach (var theme in AppearanceThemes.All)
            {
                main.Preferences.Theme = theme.Id; main.Preferences.Accent = theme.Accent; main.ApplyAppearance();
                Check(((SolidColorBrush)Application.Current.FindResource("Bg")).Color == (Color)ColorConverter.ConvertFromString(theme.Bg), "theme applies: " + theme.Name);
            }
            main.Preferences.AlbumViewMode = "grid"; main.Preferences.ArtistViewMode = "grid";
            var real = main.Library.Tracks.ToList();
            for (var i = 0; i < 500; i++) main.Library.Tracks.Add(new Track { Id = "virtual-" + i, Path = a1.Path, Title = "Track " + i, Album = "Album " + i.ToString("000"), Artist = "Artist " + i.ToString("000"), Seconds = 120 });
            main.Navigate("albums"); main.UpdateLayout(); await Task.Delay(100);
            var galleryBar = Visuals<System.Windows.Controls.Primitives.ScrollBar>(main.AlbumItems).First(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var galleryThumb = Visuals<System.Windows.Controls.Primitives.Thumb>(galleryBar).First();
            var thumbBody = Visuals<Border>(galleryThumb).First();
            Check(galleryThumb.ActualHeight >= 28 && thumbBody.ActualHeight >= 26 && thumbBody.ActualWidth == 6, "large gallery thumb renders a complete vertical pill instead of a clipped cap");
            SelfTests.Capture(main, "lune-scrollbar-top.png");
            var galleryScroll = Visuals<ScrollViewer>(main.AlbumItems).First(); galleryScroll.ScrollToBottom(); main.UpdateLayout(); await Task.Delay(100);
            Check(thumbBody.ActualHeight >= 26 && galleryScroll.VerticalOffset > 0, "gallery thumb retains full shape at bottom");
            SelfTests.Capture(main, "lune-scrollbar-bottom.png");
            main.LayoutToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.UpdateLayout();
            Check(main.CollectionList.Visibility == Visibility.Visible && main.CollectionList.Items.Count == 502 && main.AlbumItems.ItemsSource == null, "album list mode includes all albums and releases gallery visuals");
            var panel = Visuals<VirtualizingStackPanel>(main.CollectionList).First(); var scroll = Visuals<ScrollViewer>(main.CollectionList).First();
            Check(panel.Children.Count < 60, "small-thumbnail album list remains virtualized");
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            scroll.ScrollToBottom(); main.UpdateLayout(); await Wait(() => main.CollectionList.ItemContainerGenerator.ContainerFromIndex(501) != null, "last album remains reachable by scrolling");
            main.Preferences.Theme = "Snow"; main.Preferences.Accent = AppearanceThemes.Find("Snow").Accent; main.ApplyAppearance(); SelfTests.Capture(main, "lune-album-list-light.png");
            main.Navigate("artists"); Check(main.CollectionList.Visibility == Visibility.Collapsed, "artist layout preference is independent of album layout");
            main.LayoutToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); main.UpdateLayout(); Check(main.CollectionList.Items.Count == 501, "artist list shows all artists");
            main.Preferences.Theme = "Forest"; main.Preferences.Accent = AppearanceThemes.Find("Forest").Accent; main.ApplyAppearance(); SelfTests.Capture(main, "lune-artist-list-dark.png"); main.SaveSettings();
            settings = new Store().Load<Settings>("settings.json"); Check(settings.AlbumViewMode == "list" && settings.ArtistViewMode == "list" && settings.Theme == "Forest", "layouts and selected theme persist");
            main.Library.Tracks.Clear(); main.Library.Tracks.AddRange(real); main.Navigate("albums");
            main.UpdateLayout(); var rowButton = Visuals<Button>(main.CollectionList).First(); rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(main.TrackGrid.Visibility == Visibility.Visible && main.TrackGrid.Items.Count > 0, "small-thumbnail row opens album tracks");
            var analyzer = new SpectrumAnalyzer(); var low = Sine(750); var high = Sine(5000); var levels = new float[SpectrumAnalyzer.BandCount];
            analyzer.Analyze(low, 0, low.Length, 1, 48000); analyzer.CopyLevels(levels); Check(levels.All(v => v == 0), "spectrum processing is off by default");
            analyzer.Enabled = true; analyzer.Analyze(low, 0, low.Length, 1, 48000); analyzer.CopyLevels(levels); var lowBand = Array.IndexOf(levels, levels.Max());
            analyzer.Reset(); analyzer.Analyze(high, 0, high.Length, 1, 48000); analyzer.CopyLevels(levels); var highBand = Array.IndexOf(levels, levels.Max());
            Check(levels.Max() > .3 && highBand > lowBand, "spectrum measures actual audio frequencies"); analyzer.Enabled = false; analyzer.CopyLevels(levels); Check(levels.All(v => v == 0), "disabling spectrum clears its levels");
            main.Preferences.Theme = "Charcoal"; main.Preferences.Accent = "#CBEB9B"; main.ApplyAppearance(); main.Navigate("tracks"); main.PlayTrack(a1); main.UpdateLayout();
            Check(Visuals<MarqueeText>(main).Any(t => !t.OnHover && t.IsScrolling), "long now-playing title starts slow scrolling");
            await MarqueeChecks.CheckTail(Visuals<MarqueeText>(main).First(t => !t.OnHover && t.IsScrolling), Check, "lune-title-main-end.png");
            main.P.SpectrumEnabled = true; await Task.Delay(700); main.P.Spectrum.CopyLevels(levels);
            Check(levels.Max() > 0 && Visuals<SpectrumView>(main).Single().IsAnimating, "visible player visualizer follows live decoder audio"); SelfTests.Capture(main, "lune-spectrum-main.png");
            main.ShowMini(); await Task.Delay(400); var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
            Check(!Visuals<SpectrumView>(main).Single().IsAnimating && Visuals<SpectrumView>(mini).Single().IsAnimating, "only visible mini visualizer runs");
            Check(Visuals<MarqueeText>(main).All(t => !t.IsScrolling) && Visuals<MarqueeText>(mini).Any(t => t.IsScrolling), "hidden main marquee stops while mini title scrolls"); SelfTests.Capture(mini, "lune-spectrum-mini.png");
            await MarqueeChecks.CheckTail(Visuals<MarqueeText>(mini).First(t => t.IsScrolling), Check, "lune-title-mini-end.png"); SelfTests.Capture(mini, "lune-mini-title-end.png");
            mini.WindowState = WindowState.Minimized; await Task.Delay(100);
            Check(!Visuals<SpectrumView>(mini).Single().IsAnimating && Visuals<MarqueeText>(mini).All(t => !t.IsScrolling) && !main.P.Spectrum.Enabled, "minimized player suspends marquee, visualization and audio analysis");
            mini.WindowState = WindowState.Normal; await Task.Delay(100);
            main.P.Toggle(); Check(!Visuals<SpectrumView>(mini).Single().IsAnimating && !main.P.Spectrum.Enabled, "pause stops visualization timer and audio analysis");
            main.P.SpectrumEnabled = false; main.RestoreMain(); main.SaveSettings(); Check(!new Store().Load<Settings>("settings.json").SpectrumEnabled, "visualizer preference persists");
            main.Preferences.Theme = "Snow"; main.Preferences.Accent = AppearanceThemes.Find("Snow").Accent; main.ApplyAppearance();
            var config = new SettingsWindow(main); config.Show(); await Task.Delay(100);
            var themeSelector = Visuals<ComboBox>(config).Single(c => c.Items.Count == 20); themeSelector.BringIntoView(); await Task.Delay(100); SelfTests.Capture(config, "lune-theme-settings.png"); config.Close();
            File.WriteAllLines(Path.Combine(output, "enhancements-tests.txt"), results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(output, "enhancements-tests.txt"), results); Application.Current.Shutdown(1); }
    }
    private static float[] Sine(double frequency) => Enumerable.Range(0, 48000).Select(i => (float)(.5 * Math.Sin(2 * Math.PI * frequency * i / 48000))).ToArray();
    private static void CreateTone(string path, int seconds, double frequency)
    {
        SelfTests.CreateWave(path, seconds); using var stream = new FileStream(path, FileMode.Open, FileAccess.Write); stream.Position = 44; using var writer = new BinaryWriter(stream);
        for (var i = 0; i < 22050 * seconds; i++) writer.Write((short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 22050)));
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Visuals<T>(child)) yield return nested; }
    }
}
