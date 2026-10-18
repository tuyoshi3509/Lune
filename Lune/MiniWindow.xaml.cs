using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lune;
public partial class MiniWindow : Window
{
    private readonly MainWindow main;
    internal SeekInteraction SeekControl { get; }
    private bool closing;
    public MiniWindow(MainWindow main)
    {
        this.main = main; InitializeComponent(); DataContext = main;
        Icon = main.Icon;
        SeekControl = SeekInteraction.Attach(SeekSlider, main.P, main.SaveSettings);
        var p = main.Preferences; var area = SystemParameters.WorkArea;
        Left = p.MiniLeft < 0 ? area.Right - Width - 24 : Math.Clamp(p.MiniLeft, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = p.MiniTop < 0 ? area.Bottom - Height - 24 : Math.Clamp(p.MiniTop, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        ApplyAppearance();
        LocationChanged += (_, _) => { p.MiniLeft = Left; p.MiniTop = Top; };
    }
    public void ApplyAppearance() { Opacity = main.Preferences.Opacity; Topmost = main.Preferences.MiniTopmost; UpdateToggleColors(); }
    public void UpdateToggleColors()
    {
        if (ShuffleButton == null) return;
        ShuffleButton.SetResourceReference(ForegroundProperty, main.P.Shuffle ? "Accent" : "Muted");
        RepeatButton.SetResourceReference(ForegroundProperty, main.P.Repeat != 0 ? "Accent" : "Muted");
        PrimaryMuteButton.SetResourceReference(ForegroundProperty, main.P.PrimaryOutputMuted ? "Accent" : "Muted");
        SecondaryMuteButton.SetResourceReference(ForegroundProperty, main.P.SecondaryOutputMuted ? "Accent" : "Muted");
        PinButton.ToolTip = Topmost
            ? "常に手前に表示：オン（クリックでオフ）"
            : "常に手前に表示：オフ（クリックでオン）";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, Topmost ? "常に手前に表示：オン" : "常に手前に表示：オフ");
    }
    private void Play_Click(object sender, RoutedEventArgs e) => main.TogglePlayback();
    private void Stop_Click(object sender, RoutedEventArgs e) => main.P.Stop();
    private void Next_Click(object sender, RoutedEventArgs e) => main.P.Next();
    private void Previous_Click(object sender, RoutedEventArgs e) => main.P.Previous();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => main.P.Shuffle = !main.P.Shuffle;
    private void Repeat_Click(object sender, RoutedEventArgs e) => main.P.Repeat = (main.P.Repeat + 1) % 3;
    private void Mute_Click(object sender, RoutedEventArgs e) => main.P.Muted = !main.P.Muted;
    private void PrimaryMute_Click(object sender, RoutedEventArgs e) => main.P.PrimaryOutputMuted = !main.P.PrimaryOutputMuted;
    private void SecondaryMute_Click(object sender, RoutedEventArgs e) => main.P.SecondaryOutputMuted = !main.P.SecondaryOutputMuted;
    private void OutputDevice_Click(object sender, RoutedEventArgs e) => main.ShowAudioOutputs(this);
    private void Favorite_Click(object sender, RoutedEventArgs e) => main.ToggleFavorite(main.P.Current);
    private void Expand_Click(object sender, RoutedEventArgs e) => main.RestoreMain();
    private void Close_Click(object sender, RoutedEventArgs e) => main.ExitApp();
    private void Pin_Click(object sender, RoutedEventArgs e) { main.Preferences.MiniTopmost = !Topmost; ApplyAppearance(); main.SaveSettings(); }
    private void Drag_Header(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock || e.OriginalSource is Grid) { if (e.ClickCount == 2) main.RestoreMain(); else DragMove(); main.SaveSettings(); } }
    private void Mini_Closing(object? sender, CancelEventArgs e) { if (!closing) { e.Cancel = true; Dispatcher.BeginInvoke(main.ExitApp); } }
    public void Shutdown() { closing = true; Close(); }
    private void Mini_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { main.TogglePlayback(); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.M) { main.RestoreMain(); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Up) { main.P.Volume += main.P.VolumeStep; e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Down) { main.P.Volume -= main.P.VolumeStep; e.Handled = true; }
    }
    private void Picker_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Window { Owner = this, Width = 420, Height = 510, Title = "曲を選択", WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var stack = Dialogs.Shell(picker, "曲を選択", "曲やマイリストを選んで再生");
        var filter = new ComboBox { Margin = new Thickness(0, 14, 0, 8), SelectedIndex = 0 };
        filter.Items.Add("すべての曲"); filter.Items.Add("お気に入り"); foreach (var p in main.Library.Playlists) filter.Items.Add(p);
        stack.Children.Add(filter);
        var search = new TextBox { ToolTip = "曲名・アルバム・アーティストで検索", Margin = new Thickness(0, 0, 0, 8) }; stack.Children.Add(search);
        var list = new ListBox { Height = 230, DisplayMemberPath = "Title" }; stack.Children.Add(list);
        void Refresh()
        {
            var tracks = filter.SelectedItem is Playlist p ? p.TrackIds.Select(id => main.Library.Tracks.FirstOrDefault(t => t.Id == id)).Where(t => t != null).Cast<Track>() : filter.SelectedIndex == 1 ? main.Library.Tracks.Where(t => t.Favorite) : main.Library.Tracks;
            list.ItemsSource = SearchService.Filter(tracks, search.Text, 0).ToList();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) }; timer.Tick += (_, _) => { timer.Stop(); Refresh(); };
        search.TextChanged += (_, _) => { timer.Stop(); timer.Start(); }; filter.SelectionChanged += (_, _) => Refresh();
        void Play() { if (list.SelectedItem is Track t) { if (filter.SelectedItem is Playlist p) main.PlayPlaylist(p, t); else main.P.PlayCollection(list.Items.Cast<Track>(), t); picker.Close(); } }
        list.MouseDoubleClick += (_, _) => Play(); list.KeyDown += (_, a) => { if (a.Key == Key.Enter) Play(); };
        stack.Children.Add(Dialogs.Button("▶  選んだ曲から再生", Play, true));
        var timedPlay = Dialogs.Button("時計の時間帯に合わせて再生", () => { if (filter.SelectedItem is Playlist p) { main.PlayPlaylist(p); picker.Close(); } });
        timedPlay.Visibility = Visibility.Collapsed; filter.SelectionChanged += (_, _) => timedPlay.Visibility = filter.SelectedItem is Playlist { ScheduleEnabled: true } ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(timedPlay); picker.Closed += (_, _) => timer.Stop(); Refresh(); picker.ShowDialog();
    }
}
