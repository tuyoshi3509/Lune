using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace Lune;

internal static class StartupImportChecks
{
    internal static void Prepare(bool mini)
    {
        var store = new Store();
        store.Save("settings.json", new Settings { Folders = new(), StartMini = mini, MediaKeys = false, Muted = true });
        Track Song(string id, string artist, string album, bool youtube = false, string albumArtist = "") => new() {
            Id = id, Path = Path.Combine(Paths.Data, id + ".mp3"), Title = "確認用の曲 " + id, Artist = artist,
            Album = album, AlbumArtist = albumArtist, ImportedFromYouTube = youtube, AddedUtcTicks = DateTime.UtcNow.Ticks
        };
        store.Save("library.json", new LibraryState { Tracks = new() {
            Song("a", "Lune Band", "First Album", true), Song("b", "Lune Band", "First Album", true),
            Song("c", "Lune Band", "新しい季節", true), Song("d", "Other Band", "別のアルバム", true),
            Song("e", "Lune Band", "Local Collection"), Song("f", "Guest", "Collaboration", false, "Lune Band"),
            Song("g", "Lune Band", "不明なアルバム"), Song("h", "ルネ", "日本語アルバム"), Song("i", "Lune Band", " ")
        } });
    }

    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>();
        var miniStart = main.Preferences.StartMini;
        var output = Path.Combine(Paths.Root, "test-output", miniStart ? "startup-mini-tests.txt" : "startup-main-tests.txt");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        void CheckMain(string name) {
            Check(main.IsVisible && IsWindowVisible(new WindowInteropHelper(main).Handle) && main.ShowInTaskbar && main.WindowState != WindowState.Minimized, name);
            Check(!Application.Current.Windows.OfType<MiniWindow>().Any(w => w.IsVisible || IsWindowVisible(new WindowInteropHelper(w).Handle)), "main view leaves no visible mini window");
            Check(main.AlbumItems.Items.Count > 0 || main.CollectionList.Items.Count > 0, "restored main gallery has content");
        }
        void CheckMini(string name) {
            var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
            Check(mini.IsVisible && IsWindowVisible(new WindowInteropHelper(mini).Handle) && mini.ShowInTaskbar, name);
            Check(!main.IsVisible && !IsWindowVisible(new WindowInteropHelper(main).Handle) && !main.ShowInTaskbar, "mini view leaves no blank native main window or main taskbar entry");
        }
        try
        {
            await Task.Delay(250);
            Check(!new Settings().StartMini, "default startup is the main window");
            if (miniStart) CheckMini("explicit mini startup displays mini"); else CheckMain("default startup displays main");
            main.RestoreMain(); await Task.Delay(100); CheckMain("main restored after startup");
            main.SetStatus("画面切り替えで起動処理を繰り返さないことを確認");
            for (var i = 0; i < 3; i++) {
                main.ShowMini(); await Task.Delay(90); CheckMini("switch to mini " + i);
                main.RestoreMain(); await Task.Delay(90); CheckMain("switch to main " + i);
            }
            Check(main.StatusText.Text == "画面切り替えで起動処理を繰り返さないことを確認", "switching views does not repeat startup or reset session");
            main.ShowMini();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8))) {
                var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                launch.ArgumentList.Add("--data"); launch.ArgumentList.Add(Paths.Data);
                using var second = Process.Start(launch)!; await second.WaitForExitAsync(timeout.Token);
                Check(second.ExitCode == 0, "second launch signals existing instance and exits");
            }
            await Task.Delay(200); CheckMain("second launch restores only main");
            main.WindowState = WindowState.Minimized; main.RestoreMain(); await Task.Delay(90); CheckMain("minimized main restores normally");
            SelfTests.Capture(main, "lune-startup-main.png");

            var names = ImportAlbumSuggestions.Find(main.Library.Tracks, "  ＬＵＮＥ ＢＡＮＤ  ");
            Check(names.Count == 4 && names.Contains("First Album") && names.Contains("新しい季節"), "matching artist ignores case, width and surrounding spaces and removes duplicate albums");
            Check(names.Contains("Local Collection") && names.Contains("Collaboration") && !names.Contains("別のアルバム"), "candidates include local and album-artist matches, exclude other artists");
            Check(!names.Contains("不明なアルバム") && !names.Contains(""), "empty and unknown album names are excluded");
            Check(ImportAlbumSuggestions.Find(main.Library.Tracks, "").Count == 0 && ImportAlbumSuggestions.Find(main.Library.Tracks, "Nobody").Count == 0, "blank and new artists have no suggestions");
            Check(ImportAlbumSuggestions.Find(main.Library.Tracks, "ﾙﾈ").SequenceEqual(new[] { "日本語アルバム" }), "Japanese width differences match existing artist");

            var import = new ImportWindow(main); import.Show(); await Task.Delay(100);
            var album = import.AlbumInput; var artist = import.ArtistInput;
            import.ApplyMetadata(new VideoInfo("", "取得された曲名", "Lune Band", "動画が提供したアルバム", "", 120));
            Check(album.Items.Count == 4 && album.Text == "動画が提供したアルバム", "fetched artist populates candidates without replacing fetched album");
            var editor = (TextBox)album.Template.FindName("PART_EditableTextBox", album);
            Check(editor.IsVisible && editor.Text == album.Text, "editable album template renders metadata");
            editor.Text = "手入力した新しいアルバム";
            Check(album.Text == editor.Text, "typing in album editor updates value used by import");
            artist.Text = "Other Band";
            Check(album.Items.Count == 1 && album.Text == "手入力した新しいアルバム", "artist edit refreshes list and preserves manual text");
            album.SelectedIndex = 0;
            Check(album.Text == "別のアルバム" && editor.Text == album.Text, "choosing an existing album sets editable text");
            artist.Text = "Unknown";
            Check(album.Items.Count == 0 && album.Text == "別のアルバム", "refreshing selected album does not erase it");
            artist.Text = "Lune Band"; album.SelectedItem = "新しい季節";
            album.BringIntoView(); album.IsDropDownOpen = true; await Task.Delay(100);
            var popup = (Popup)album.Template.FindName("PART_Popup", album);
            Check(popup.IsOpen && ((FrameworkElement)popup.Child).ActualHeight > 30, "album candidates open in a visible list");
            SelfTests.Capture(import, "lune-import-album-candidates.png"); SelfTests.Capture((FrameworkElement)popup.Child, "lune-album-candidates-dropdown.png");
            import.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(import), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!album.IsDropDownOpen && import.IsVisible, "Escape closes candidate list without closing import dialog");
            editor.Text = "確定前の手入力"; album.IsDropDownOpen = true; await Task.Delay(50); album.IsDropDownOpen = false;
            Check(album.Text == "確定前の手入力", "opening and dismissing candidates leaves free text intact");
            main.Library.Tracks.Add(new Track { Id = "new", Artist = "Lune Band", Album = "今回追加したアルバム" });
            album.IsDropDownOpen = true; await Task.Delay(50);
            Check(album.Items.Contains("今回追加したアルバム") && album.Text == "確定前の手入力", "reopening suggestions includes newly registered albums");
            album.IsDropDownOpen = false;
            main.Preferences.Theme = "Snow"; main.Preferences.Accent = AppearanceThemes.Find("Snow").Accent; main.ApplyAppearance();
            await Task.Delay(70); SelfTests.Capture(import, "lune-import-album-light.png"); import.Close();
            var settings = new SettingsWindow(main); settings.Show(); await Task.Delay(90);
            var devices = settings.DeviceSelector; devices.IsDropDownOpen = true; await Task.Delay(60);
            Check(!((TextBox)devices.Template.FindName("PART_EditableTextBox", devices)).IsVisible && devices.SelectedItem != null, "non-editable device dropdown still displays selection");
            devices.IsDropDownOpen = false; settings.Close();
            File.WriteAllLines(output, results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(output, results); Application.Current.Shutdown(1); }
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
