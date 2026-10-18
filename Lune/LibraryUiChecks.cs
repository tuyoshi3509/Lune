using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lune;

internal static class LibraryUiChecks
{
    internal static async Task<List<string>> Run(MainWindow main, string fixtures)
    {
        var results = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        var folderA = Path.Combine(fixtures, "Library A"); var folderB = Path.Combine(fixtures, "Library B");
        Directory.CreateDirectory(folderA); Directory.CreateDirectory(folderB);
        var pathA = Path.Combine(folderA, "登録解除.wav"); var pathB = Path.Combine(folderB, "ごみ箱テスト.wav");
        SelfTests.CreateWave(pathA, 20); SelfTests.CreateWave(pathB, 20);
        await main.AddPathsAsync(new[] { folderA, folderB });
        var a = main.Library.Tracks.Single(t => t.Path == pathA); var b = main.Library.Tracks.Single(t => t.Path == pathB);
        Check(main.Preferences.Folders.Contains(folderA) && main.Preferences.Folders.Contains(folderB), "multiple library paths scan in one addition");
        Check(new Store().Load<Settings>("settings.json").Folders.Contains(folderB), "multiple library paths persist");
        var source = Path.Combine(fixtures, "source-url.m4a"); await main.RegisterDownload(source);
        var youtube = main.Library.Tracks.Single(t => t.Path == source);
        Check(youtube.IsYouTube && youtube.HasSourceUrl, "YouTube registration preserves origin and source URL");
        main.Navigate("youtube"); Check(main.TrackGrid.Items.Cast<Track>().All(t => t.IsYouTube) && main.TrackGrid.Items.Contains(youtube), "YouTube view contains only imported tracks");
        main.Navigate("tracks"); Check(main.TrackGrid.Items.Contains(youtube) && main.TrackGrid.Items.Contains(a), "YouTube and local tracks coexist in normal library");
        main.Navigate("albums"); Check(main.AlbumItems.Items.Cast<AlbumCard[]>().SelectMany(x => x).Any(c => c.Tracks.Contains(youtube)), "YouTube track appears in normal albums");
        main.Navigate("artists"); Check(main.AlbumItems.Items.Cast<AlbumCard[]>().SelectMany(x => x).Any(c => c.Tracks.Contains(youtube)), "YouTube track appears in normal artists");
        var playlist = new Playlist { Name = "YouTube とローカル", TrackIds = new() { a.Id, b.Id } }; main.Library.Playlists.Add(playlist);
        main.P.SetQueue(new[] { a, b }, a, true); await Task.Delay(250);
        Check(main.RemoveLibraryTracks(new[] { a }, false).Count == 0, "unregister selection succeeds");
        Check(File.Exists(pathA) && !main.Library.Tracks.Any(t => t.Id == a.Id) && !playlist.TrackIds.Contains(a.Id), "unregister keeps original file and removes playlist entries");
        Check(main.P.Current?.Id == b.Id && !main.P.IsPlaying && main.P.Queue.All(t => t.Id != a.Id), "removing current track releases it and pauses remaining queue");
        Check(!new Store().Load<LibraryState>("library.json").Tracks.Any(t => t.Id == a.Id), "unregister persists before rescan");
        await main.ScanLibraryAsync(); a = main.Library.Tracks.Single(t => t.Id == a.Id); b = main.Library.Tracks.Single(t => t.Id == b.Id);
        Check(File.Exists(pathA), "rescan restores unregistered file inside library path");
        main.P.SetQueue(new[] { a, b }, b, true); await Task.Delay(250); main.RemoveLibraryTracks(new[] { a }, false);
        Check(main.P.Current == b && main.P.IsPlaying, "removing another track preserves active playback");
        Check(main.RemoveLibraryTracks(new[] { b }, true).Count == 0 && !File.Exists(pathB), "playing test file is released and moved to recycle bin");
        Check(main.P.Queue.Count == 0 && main.P.Current == null && !main.P.IsPlaying, "deleting final queued track clears playback state");
        var bad = new Track { Id = "directory-rejection", Path = folderB, Title = "フォルダーは削除しない" }; main.Library.Tracks.Add(bad);
        Check(main.RemoveLibraryTracks(new[] { bad }, true).Count == 1 && main.Library.Tracks.Contains(bad) && Directory.Exists(folderB), "failed file removal preserves registration and never deletes directories"); main.Library.Tracks.Remove(bad);
        await main.ScanLibraryAsync(); Check(!main.Library.Tracks.Any(t => t.Id == b.Id), "rescan does not restore recycled file");
        main.Navigate("youtube"); main.UpdateLayout(); main.TrackGrid.ScrollIntoView(youtube); main.UpdateLayout();
        var row = (DataGridRow)main.TrackGrid.ItemContainerGenerator.ContainerFromItem(youtube);
        Check(Visuals<Button>(row).Any(button => button.Tag is Track t && t.Id == youtube.Id && button.ToolTip as string == "元動画を開く" && button.IsEnabled), "YouTube row exposes source video button");
        main.P.SetQueue(new[] { youtube }, youtube, false);
        main.Width = 1200; main.Height = 800; await Task.Delay(100); SelfTests.Capture(main, "lune-youtube.png");
        main.Width = 980; main.Height = 640; main.UpdateLayout(); main.SidebarScroll.ScrollToEnd(); await Task.Delay(100); SelfTests.Capture(main, "lune-small-window.png");
        Check(main.SidebarScroll.ScrollableHeight > 0 && main.PlaylistList.ActualHeight > 0, "short window keeps sidebar controls reachable with scrolling");
        main.Width = 1200; main.Height = 800; main.SidebarScroll.ScrollToTop(); main.Navigate("tracks"); main.UpdateLayout();
        main.PopulateTrackMenu(new() { youtube }); var menu = main.TrackGrid.ContextMenu;
        menu.PlacementTarget = main.TrackGrid; menu.Placement = PlacementMode.Center; menu.IsOpen = true; await Task.Delay(120); menu.UpdateLayout();
        Check(menu.ActualWidth >= 200 && menu.Items.OfType<MenuItem>().All(item => item.ActualHeight >= 34), "track context menu has full readable rows"); SelfTests.Capture(menu, "lune-context-menu.png");
        var submenu = menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "マイリストに追加"); submenu.IsSubmenuOpen = true; await Task.Delay(120);
        var popup = (Popup)submenu.Template.FindName("PART_Popup", submenu);
        Check(popup.IsOpen && popup.Child is FrameworkElement, "playlist submenu opens with custom template"); SelfTests.Capture((FrameworkElement)popup.Child, "lune-context-submenu.png");
        var target = submenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == playlist.Name); target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, target));
        Check(playlist.TrackIds.Contains(youtube.Id), "playlist submenu action still adds selected track"); menu.IsOpen = false;
        var checkedMenu = new ContextMenu { PlacementTarget = main, Placement = PlacementMode.Center };
        checkedMenu.Items.Add(new MenuItem { Header = "Windowsの既定のデバイス", IsCheckable = true, IsChecked = true }); checkedMenu.Items.Add(new Separator()); checkedMenu.Items.Add(new MenuItem { Header = "使用できないデバイス", IsEnabled = false });
        checkedMenu.IsOpen = true; await Task.Delay(100); SelfTests.Capture(checkedMenu, "lune-menu-checkmark.png"); checkedMenu.IsOpen = false;
        main.SortSelector.IsDropDownOpen = true; await Task.Delay(100); var drop = (Popup)main.SortSelector.Template.FindName("PART_Popup", main.SortSelector);
        SelfTests.Capture((FrameworkElement)drop.Child, "lune-sort-dropdown.png"); main.SortSelector.SelectedValue = "addedDesc"; main.SortSelector.IsDropDownOpen = false;
        Check(main.Preferences.TrackSort == "addedDesc", "dropdown selection updates sorting");
        var settings = new SettingsWindow(main); settings.Show(); await Task.Delay(100); settings.DeviceSelector.IsDropDownOpen = true; await Task.Delay(100);
        var devices = (Popup)settings.DeviceSelector.Template.FindName("PART_Popup", settings.DeviceSelector); SelfTests.Capture((FrameworkElement)devices.Child, "lune-device-dropdown.png"); settings.DeviceSelector.IsDropDownOpen = false;
        Visuals<CheckBox>(settings).First().BringIntoView(); await Task.Delay(100); SelfTests.Capture(settings, "lune-settings-checks.png");
        var settingsScroll = Visuals<ScrollViewer>(settings).First();
        var pathHeading = Visuals<TextBlock>(settings).First(t => t.Text == "ライブラリパス");
        settingsScroll.ScrollToVerticalOffset(settingsScroll.VerticalOffset + pathHeading.TranslatePoint(new Point(), settingsScroll).Y - 20);
        await Task.Delay(100); SelfTests.Capture(settings, "lune-library-paths.png"); settings.Close();
        var deletion = new RemoveTracksWindow(main, new[] { youtube }); deletion.Show(); await Task.Delay(100); SelfTests.Capture(deletion, "lune-delete-options.png"); deletion.Close();
        Check(deletion.DeleteFiles == null && File.Exists(source), "closing deletion dialog leaves files unchanged");
        main.ShowMini(); await Task.Delay(100); SelfTests.Capture(Application.Current.Windows.OfType<MiniWindow>().Single(), "lune-mini-updated.png"); main.RestoreMain();
        return results;
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
}
