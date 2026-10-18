using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

internal static class InteractionChecks
{
    internal static async Task Run(MainWindow main, Track[] tracks, Action<bool, string> check)
    {
        main.PlayTrack(tracks[0]); main.UpdateLayout();
        async Task Seek(Slider slider, SeekInteraction control, string label)
        {
            slider.UpdateLayout();
            control.Begin(new Point(slider.ActualWidth * .7, slider.ActualHeight / 2));
            var target = slider.Value;
            await Task.Delay(650);
            check(Math.Abs(slider.Value - target) < .01, label + " pointer position is not overwritten by playback ticks");
            control.Finish(); await Task.Delay(650);
            check(main.P.Position >= target && main.P.Position < target + 2 && main.P.IsPlaying, label + $" click commits forward seek and continues playback (target={target}, actual={main.P.Position}, playing={main.P.IsPlaying}, width={slider.ActualWidth})");
            control.Begin(new Point(slider.ActualWidth * .5, slider.ActualHeight / 2));
            control.Move(new Point(slider.ActualWidth * .2, slider.ActualHeight / 2));
            target = slider.Value; control.Finish(); await Task.Delay(650);
            check(main.P.Position >= target && main.P.Position < target + 2 && target < main.P.Duration * .3, label + " drag commits backward seek");
            check(BindingOperations.IsDataBound(slider, Slider.ValueProperty), label + " restores progress binding");
        }
        await Seek(main.SeekSlider, main.SeekControl, "main");
        var mini = new MiniWindow(main); mini.Show(); mini.UpdateLayout();
        await Seek(mini.SeekSlider, mini.SeekControl, "mini"); mini.Shutdown();
        main.P.Toggle(); main.SeekControl.Begin(new Point(main.SeekSlider.ActualWidth * .4, 12)); main.SeekControl.Finish();
        check(!main.P.IsPlaying && main.P.Position > 6, "paused seek preserves pause");
        // Reproduce handled routed input, rather than invoking Begin directly:
        // Slider class handlers run before an instance's normal event handlers.
        main.SeekSlider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Handled = true });
        check(!BindingOperations.IsDataBound(main.SeekSlider, Slider.ValueProperty), "handled pointer-down still enters seek interaction");
        main.SeekSlider.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Handled = true });
        check(BindingOperations.IsDataBound(main.SeekSlider, Slider.ValueProperty) && !main.P.IsPlaying, "handled pointer-up commits seek and restores binding without autoplay");
        var menu = new ContextMenu();
        var card = new AlbumCard { Title = tracks[0].Album, Tracks = tracks.Reverse().ToList() };
        main.PopulateCollectionMenu(menu, card);
        MenuItem Item(string name) => menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == name);
        void Click(string name) => Item(name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(Item("アルバムの詳細") != null && Item("保存場所を開く") != null && Item("ライブラリから削除…") != null, "album menu exposes details, actual location and removal");
        menu.PlacementTarget = main.TrackGrid; menu.IsOpen = true; await Task.Delay(100);
        SelfTests.Capture(menu, "lune-album-context-menu.png"); menu.IsOpen = false;
        var details = new ArtistDetailsWindow(main, card.Title, tracks, "アルバム"); details.Show(); await Task.Delay(80);
        check(details.Title.StartsWith("アルバムの詳細") && details.Stats.Rows.Count == tracks.Length, "album details summarize only the album tracks");
        SelfTests.Capture(details, "lune-album-listening-details.png"); details.Close();
        Click("再生"); check(main.P.PlaybackOrder.SequenceEqual(tracks) && main.P.Queue.Count == 0, "album menu plays disc/track order without queue insertion");
        var playlist = new Playlist { Name = "Album menu test" }; main.Library.Playlists.Add(playlist);
        main.PopulateCollectionMenu(menu, card);
        Item("マイリストに追加").Items.OfType<MenuItem>().Single(i => (string)i.Header == playlist.Name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(playlist.TrackIds.SequenceEqual(tracks.Select(t => t.Id)), "album menu adds all tracks to selected playlist in order");
        Click("お気に入りに追加 / 解除"); check(tracks.All(t => t.Favorite), "album favorite action affects all tracks");
        Click("お気に入りに追加 / 解除"); check(tracks.All(t => !t.Favorite), "album favorite removal affects all tracks");
        Click("次に再生"); check(main.P.Queue.SequenceEqual(tracks), "album next action queues whole album in order");
        while (main.P.Queue.Count > 0) main.P.RemoveAt(0);
        var split = new AlbumCard { Tracks = new() { tracks[0], new Track { Path = System.IO.Path.Combine(Paths.Data, "another", "日本語 , name.wav") } } };
        main.PopulateCollectionMenu(menu, split);
        check(Item("保存場所を開く").Items.Count == 2, "split album locations are selectable without opening many windows");
        main.PopulateTrackMenu(new() { tracks[0] });
        check(main.TrackGrid.ContextMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == "保存場所を開く"), "track menu uses renamed location action");
        main.Library.Playlists.Remove(playlist); main.P.Stop();
    }
}
