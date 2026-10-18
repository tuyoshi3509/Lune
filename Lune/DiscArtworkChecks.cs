using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Lune;

internal static class DiscArtworkChecks
{
    internal static async Task Run(MainWindow main, Track[] tracks, Action<bool, string> check)
    {
        var original = tracks.Select(t => t.Artwork).ToArray();
        var cover = Path.Combine(Paths.Root, "Lune", "Assets", "Lune.png");
        // An old portable installation path must resolve to its hash-named local cache.
        var cachedCover = LibraryService.SaveArtwork(File.ReadAllBytes(cover));
        var movedCover = Path.Combine(Paths.Data, "missing-old-install", Path.GetFileName(cachedCover));
        check(AlbumArtwork.Resolve(movedCover) == cachedCover, "moved portable artwork resolves to current cache without rescanning");
        var other = new Track { Artist = "別アーティスト", Album = tracks[0].Album };
        tracks[0].Artwork = ""; tracks[1].Artwork = cover; tracks[2].Artwork = "own-cover";
        AlbumArtwork.Refresh(tracks.Append(other));
        check(tracks[0].DisplayArtwork == cover && tracks[0].Artwork == "", "missing track artwork inherits its album without changing stored artwork");
        check(tracks[2].DisplayArtwork == "own-cover" && other.DisplayArtwork == "", "own artwork takes priority and other artists do not leak artwork");
        check(!JsonSerializer.Serialize(tracks[0]).Contains("DisplayArtwork"), "display fallback is not persisted as per-track metadata");
        tracks[1].Artwork = ""; tracks[2].Artwork = ""; AlbumArtwork.Refresh(tracks);
        check(tracks[0].DisplayArtwork == "", "removing last album cover clears fallback");
        tracks[1].Artwork = cover; main.SaveLibrary(); main.PlayTrack(tracks[0]); main.UpdateLayout(); await Task.Delay(150);
        var expected = (ImageSource)new ArtworkConverter().Convert(cover, typeof(ImageSource), null!, CultureInfo.InvariantCulture);
        check(ReferenceEquals(main.CurrentDisc.Source, expected), "main player displays sibling album artwork on artless track");
        var angle = main.CurrentDisc.Angle; await Task.Delay(220);
        check(main.CurrentDisc.IsSpinning && main.CurrentDisc.Angle > angle, "disc rotates during actual playback");
        SelfTests.Capture(main, "lune-disc-main.png");
        main.P.Toggle(); angle = main.CurrentDisc.Angle; await Task.Delay(180);
        check(!main.CurrentDisc.IsSpinning && Math.Abs(main.CurrentDisc.Angle - angle) < .001, "pause removes animation and retains disc angle");
        main.P.Toggle(); main.ShowMini(); await Task.Delay(180);
        var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
        main.Preferences.MiniTopmost = true; mini.ApplyAppearance();
        check(mini.Topmost && mini.PinButton.IsChecked == true, "pin is pressed when always-on-top is enabled");
        mini.PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(!mini.Topmost && mini.PinButton.IsChecked == false && !main.Preferences.MiniTopmost, "pin click disables topmost and clears pressed state");
        mini.PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(mini.Topmost && mini.PinButton.IsChecked == true && main.Preferences.MiniTopmost, "second pin click enables topmost and restores pressed state");
        check(!main.CurrentDisc.IsSpinning && mini.CurrentDisc.IsSpinning && ReferenceEquals(mini.CurrentDisc.Source, expected), "only visible mini disc spins and uses album fallback");
        SelfTests.Capture(mini, "lune-disc-mini.png");
        angle = mini.CurrentDisc.Angle; await Task.Delay(250); check(mini.CurrentDisc.Angle > angle, "mini disc angle advances");
        mini.WindowState = WindowState.Minimized; await Task.Delay(100);
        check(!mini.CurrentDisc.IsSpinning, "minimized mini removes disc animation");
        mini.WindowState = WindowState.Normal; main.P.Stop(); await Task.Delay(100);
        check(!mini.CurrentDisc.IsSpinning, "stop removes disc animation");
        main.RestoreMain(); main.P.Toggle(); await Task.Delay(100);
        check(main.CurrentDisc.IsSpinning && !mini.CurrentDisc.IsSpinning, "restoring main spins only the visible disc");
        main.WindowState = WindowState.Minimized; await Task.Delay(100); check(!main.CurrentDisc.IsSpinning, "minimized main removes disc animation");
        main.WindowState = WindowState.Normal; main.P.Stop();
        for (var i = 0; i < tracks.Length; i++) tracks[i].Artwork = original[i];
        main.SaveLibrary();
        check(tracks[0].DisplayArtwork == original[0], "album change invalidates fallback without stale cover");
    }
}
