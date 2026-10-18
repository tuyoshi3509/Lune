using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lune;
public partial class MainWindow : Window
{
    public PlayerService P { get; } = new();
    internal SeekInteraction SeekControl { get; }
    public DiscordPresence Discord { get; }
    public Settings Preferences { get; private set; }
    public LibraryState Library { get; private set; }
    public ImportArtistHistory ImportArtists { get; } = new();
    private readonly Store store = new();
    private readonly bool selfTest, smoke;
    private readonly CancellationTokenSource lifetime = new();
    public CancellationToken LifetimeToken => lifetime.Token;
    private CancellationTokenSource? scanCancel;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string view = "albums", parentView = "albums";
    private List<Track>? detailTracks;
    private List<Track> visibleTracks = new();
    private List<AlbumCard> cards = new();
    private Playlist? selectedPlaylist;
    private int albumColumns = 1;
    private bool ready, busy, exiting, playlistSync, sortSync, loaded, startupViewChosen;
    private Point dragStart;
    private Track? dragTrack;
    private string? dragPlaylistSource;
    private MiniWindow? mini;
    private System.Windows.Forms.NotifyIcon? tray;
    private readonly List<int> registeredHotkeys = new();
    private IntPtr hwnd;
    public MainWindow(bool test = false, bool smokeTest = false)
    {
        selfTest = test; smoke = smokeTest;
        Discord = new DiscordPresence(P, Environment.GetCommandLineArgs().Any(a => a.EndsWith("-test", StringComparison.Ordinal) || a is "--smoke" or "--perf"));
        Preferences = store.Load<Settings>("settings.json"); Library = store.Load<LibraryState>("library.json");
        AlbumArtwork.Refresh(Library.Tracks);
        if (!double.IsFinite(Preferences.VolumeMinimum) || !double.IsFinite(Preferences.VolumeMaximum) || Preferences.VolumeMinimum < 0 || Preferences.VolumeMaximum > 1 || Preferences.VolumeMinimum >= Preferences.VolumeMaximum)
        {
            Preferences.VolumeMinimum = 0; Preferences.VolumeMaximum = 0.3;
        }
        Preferences.Volume = Math.Clamp(double.IsFinite(Preferences.Volume) ? Preferences.Volume : 0.15, Preferences.VolumeMinimum, Preferences.VolumeMaximum);
        Preferences.Opacity = Math.Clamp(double.IsFinite(Preferences.Opacity) ? Preferences.Opacity : 1, 0.3, 1);
        if (string.IsNullOrWhiteSpace(Preferences.DownloadFolder)) Preferences.DownloadFolder = Path.Combine(Paths.Root, "Music", "Imports");
        var legacyImportRoot = Path.Combine(Paths.Root, "Music", "Imports") + Path.DirectorySeparatorChar;
        var extraFiles = Library.ExtraFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var track in Library.Tracks.Where(t => extraFiles.Contains(t.Path) && t.Path.StartsWith(legacyImportRoot, StringComparison.OrdinalIgnoreCase))) track.ImportedFromYouTube = true;
        InitializeComponent(); DataContext = this;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Lune.ico"));
        Resources["AlbumSize"] = 168d;
        Resources["AlbumRowHeight"] = 268d;
        AlbumItems.SizeChanged += (_, _) => ResizeGallery();
        P.SetVolumeRange(Preferences.VolumeMinimum, Preferences.VolumeMaximum);
        P.Volume = Preferences.Volume; P.Muted = Preferences.Muted; P.Shuffle = Preferences.Shuffle; P.Repeat = Preferences.Repeat;
        P.OutputDeviceId = Preferences.OutputDeviceId;
        P.SecondaryOutputDeviceId = Preferences.SecondaryOutputDeviceId;
        P.PrimaryOutputMuted = Preferences.PrimaryOutputMuted; P.SecondaryOutputMuted = Preferences.SecondaryOutputMuted;
        P.SpectrumEnabled = Preferences.SpectrumEnabled;
        P.AlbumTracks = AlbumPlaybackOrder;
        P.PlaylistTracks = PlaylistTracks;
        P.Error += SetStatus;
        InitializeMusicTools();
        P.TrackChanged += () => { ScheduleSave(); if (view == "queue") RefreshView(); };
        P.QueueChanged += () => { ScheduleSave(); if (view == "queue") RefreshView(); };
        P.PropertyChanged += (_, e) => { if (e.PropertyName is "Volume" or "VolumeMinimum" or "VolumeMaximum" or "Muted" or "Shuffle" or "Repeat" or "OutputDeviceId" or "SecondaryOutputDeviceId" or "PrimaryOutputMuted" or "SecondaryOutputMuted") { UpdateToggleColors(); ScheduleSave(); } };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RefreshView(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        TrackGrid.PreviewMouseLeftButtonDown += Track_MouseDown;
        TrackGrid.PreviewMouseMove += Track_MouseMove;
        SeekControl = SeekInteraction.Attach(SeekSlider, P, ScheduleSave);
        TrackGrid.PreviewDragOver += Drag_Over;
        PlaylistList.PreviewDragOver += Drag_Over;
        PlaylistList.PreviewDrop += Playlist_Drop;
        ApplyAppearance(); ready = true;
        RefreshPlaylists(); RefreshView();
        Discord.Configure(Preferences.DiscordEnabled, Preferences.DiscordApplicationId);
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        if (selfTest) { await SelfTests.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--schedule-test")) { await ScheduleChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--discord-test")) { await DiscordChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--audio-output-test")) { await AudioOutputChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--playlist-import-test")) { await PlaylistImportChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--crop-test")) { await ArtworkCropChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--enhancements-test")) { await EnhancementChecks.Run(this); return; }
        SetupTray();
        if (Library.Tracks.Count > 0)
        {
            var map = Library.Tracks.ToDictionary(t => t.Id);
            var last = map.GetValueOrDefault(Preferences.LastTrack);
            var restored = Preferences.Queue.Where(map.ContainsKey).Select(id => map[id]).ToList();
            P.Restore(Preferences.ExplicitQueue.Where(map.ContainsKey).Select(id => map[id]), restored, last, Preferences.QueuePlayback, Preferences.LastPosition);
            if (Library.Playlists.FirstOrDefault(p => p.Id == Preferences.LastScheduledPlaylistId) is { } scheduled) P.RestoreScheduledPlaylist(scheduled, PlaylistTracks(scheduled));
            SetStatus($"{Library.Tracks.Count:N0} 曲 · 前回の読み込み {Library.LastScan:MM/dd HH:mm}" + (Library.ReadErrors > 0 ? $" · 読み取り注意 {Library.ReadErrors} 件（設定で確認）" : ""));
        }
        else await ScanLibraryAsync();
        await InitializeAddedDatesAsync();
        if (store.Warning != null) SetStatus(store.Warning);
        if (P.Listening.Warning != null) SetStatus("再生履歴：" + P.Listening.Warning);
        if (Environment.GetCommandLineArgs().Contains("--rescan")) await ScanLibraryAsync();
        if (Environment.GetCommandLineArgs().Contains("--perf")) { await SelfTests.Perf(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--exit-test")) { await SelfTests.ExitOnClose(this); return; }
        // Hiding a Window during its initial Show/Loaded can leave a blank native
        // window behind. Let Show finish and the initial layout render first.
        await Dispatcher.InvokeAsync(() => {
            if (!exiting && !startupViewChosen && Preferences.StartMini && !smoke && !((App)Application.Current).ActivationRequested) ShowMini();
        }, DispatcherPriority.ContextIdle);
        if (Environment.GetCommandLineArgs().Contains("--startup-import-test")) { await StartupImportChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--activation-test")) { await SelfTests.Activation(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--distribution-test")) { await DistributionChecks.Run(this); return; }
        if (Environment.GetCommandLineArgs().Contains("--update-check-test")) { await UpdateChecks.Run(this, false); return; }
        if (Environment.GetCommandLineArgs().Contains("--update-integration-test")) { await UpdateChecks.Run(this, true); return; }
        if (smoke) { await SelfTests.Smoke(this); return; }
        await InitializeUpdates();
    }
    public async Task ScanLibraryAsync()
    {
        if (busy) return; busy = true; scanCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetStatus("ライブラリパスを確認しています…");
        try
        {
            Library = await LibraryService.ScanAsync(new Settings { Folders = Preferences.Folders.ToList() }, Library, new Progress<string>(SetStatus), scanCancel.Token);
            SaveLibrary(); RefreshPlaylists(); detailTracks = null; if (view == "detail" || view == "artistAlbums") view = "albums"; RefreshView();
            SetStatus($"{Library.Tracks.Count:N0} 曲を読み込みました" + (Library.ProtectedFiles > 0 ? $" · 保護付き {Library.ProtectedFiles} 件は対象外" : "") + (Library.ReadErrors > 0 ? $" · 読み取り注意 {Library.ReadErrors} 件" : ""));
        }
        catch (OperationCanceledException) { SetStatus("読み込みをキャンセルしました。"); }
        catch (Exception ex) { ReportError(ex); }
        finally { busy = false; scanCancel?.Dispose(); scanCancel = null; }
    }
    public void CancelScan() => scanCancel?.Cancel();
    private async Task InitializeAddedDatesAsync()
    {
        if (!Library.Tracks.Any(t => t.AddedUtcTicks <= 0)) return;
        var dates = await LibraryOrdering.EstimateExistingDatesAsync(Library.Tracks);
        if (exiting) return;
        foreach (var track in Library.Tracks)
            if (track.AddedUtcTicks <= 0 && dates.TryGetValue(track.Id, out var ticks)) { track.AddedUtcTicks = ticks; track.AddedDateEstimated = true; }
        SaveLibrary(); RefreshView();
    }
    public void SetStatus(string message) { if (!exiting) StatusText.Text = message; }
    private void ReportError(Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "操作を完了できませんでした", MessageBoxButton.OK, MessageBoxImage.Information); }
    public void SaveLibrary()
    {
        AlbumArtwork.Refresh(Library.Tracks, P.Current);
        store.Save("library.json", Library);
        P.RefreshScheduledPlaylist(Library.Playlists.FirstOrDefault(p => p.Id == P.ScheduledPlaylistId));
    }
    private void ScheduleSave() { if (!ready || exiting) return; saveTimer.Stop(); saveTimer.Start(); }
    public void SaveSettings()
    {
        if (exiting) return;
        P.SaveListeningHistory();
        Preferences.Volume = P.Volume; Preferences.Muted = P.Muted; Preferences.Shuffle = P.Shuffle; Preferences.Repeat = P.Repeat;
        Preferences.VolumeMinimum = P.VolumeMinimum; Preferences.VolumeMaximum = P.VolumeMaximum;
        Preferences.OutputDeviceId = P.OutputDeviceId;
        Preferences.SecondaryOutputDeviceId = P.SecondaryOutputDeviceId;
        Preferences.PrimaryOutputMuted = P.PrimaryOutputMuted; Preferences.SecondaryOutputMuted = P.SecondaryOutputMuted;
        Preferences.SpectrumEnabled = P.SpectrumEnabled;
        Preferences.LastTrack = P.Current?.Id ?? ""; Preferences.LastPosition = P.Position;
        Preferences.Queue = P.PlaybackOrder.Select(t => t.Id).ToList();
        Preferences.ExplicitQueue = P.Queue.Select(t => t.Id).ToList(); Preferences.QueuePlayback = P.IsQueuePlayback;
        Preferences.LastScheduledPlaylistId = P.ScheduledPlaylistId;
        store.Save("settings.json", Preferences);
    }
    public void ApplyAppearance()
    {
        AppearanceThemes.Apply(Preferences);
        Opacity = Preferences.Opacity;
        mini?.ApplyAppearance();
        if (ready) { UpdateToggleColors(); RefreshNav(); ScheduleSave(); }
    }
    private void UpdateToggleColors()
    {
        if (ShuffleButton == null) return;
        ShuffleButton.SetResourceReference(ForegroundProperty, P.Shuffle ? "Accent" : "Muted");
        RepeatButton.SetResourceReference(ForegroundProperty, P.Repeat != 0 ? "Accent" : "Muted");
        PrimaryMuteButton.SetResourceReference(ForegroundProperty, P.PrimaryOutputMuted ? "Accent" : "Muted");
        SecondaryMuteButton.SetResourceReference(ForegroundProperty, P.SecondaryOutputMuted ? "Accent" : "Muted");
        mini?.UpdateToggleColors();
    }
    private void RefreshNav()
    {
        foreach (var button in new[] { AlbumsNav, ArtistsNav, TracksNav, FavoritesNav, YouTubeNav })
        {
            var active = (string)button.Tag == view || (view is "detail" or "artistAlbums" && (string)button.Tag == parentView);
            button.SetResourceReference(BackgroundProperty, active ? "Surface" : "Sidebar"); button.SetResourceReference(ForegroundProperty, active ? "Accent" : "Text");
        }
    }
    internal List<Track> PlaylistTracks(Playlist list)
    {
        var map = Library.Tracks.ToDictionary(t => t.Id); return list.TrackIds.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }
    private List<Track> ViewTracks()
    {
        return view switch { "youtube" => Library.Tracks.Where(t => t.IsYouTube).ToList(), "favorites" => Library.Tracks.Where(t => t.Favorite).ToList(), "playlist" when selectedPlaylist != null => PlaylistTracks(selectedPlaylist), "detail" or "artistAlbums" or "utility" when detailTracks != null => detailTracks, "queue" => P.Queue.ToList(), _ => Library.Tracks };
    }
    public void RefreshView()
    {
        if (!ready) return;
        var filtered = SearchService.Filter(ViewTracks(), SearchBox.Text, SearchField.SelectedIndex).ToList();
        var isCards = view is "albums" or "artists" or "artistAlbums";
        var listMode = isCards && CollectionViewMode == "list";
        if (!isCards && view != "utility") filtered = LibraryOrdering.Tracks(filtered, CurrentSort);
        visibleTracks = filtered;
        RefreshSortSelector();
        AlbumItems.Visibility = isCards && !listMode ? Visibility.Visible : Visibility.Collapsed;
        CollectionList.Visibility = listMode ? Visibility.Visible : Visibility.Collapsed;
        LayoutToggle.Visibility = isCards ? Visibility.Visible : Visibility.Collapsed;
        LayoutToggle.Content = listMode ? "\uE80A" : "\uE8FD"; LayoutToggle.ToolTip = listMode ? "サムネイル表示に切り替え" : "リスト表示に切り替え";
        TrackGrid.Visibility = isCards ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = view is "detail" or "artistAlbums" or "utility" ? Visibility.Visible : Visibility.Collapsed;
        DetailBanner.Visibility = view == "detail" && detailTracks?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = view switch { "utility" => utilityTitle, "youtube" => "YouTube", "albums" => "アルバム", "artists" => "アーティスト", "tracks" => "すべての曲", "favorites" => "お気に入り", "playlist" => selectedPlaylist?.Name ?? "マイリスト", "queue" => "再生キュー", "artistAlbums" => detailTracks?.FirstOrDefault()?.Artist ?? "アーティスト", _ => detailTracks?.FirstOrDefault()?.Album ?? "アルバム" };
        PageTitle.ToolTip = PageTitle.Text;
        ScheduleButton.Visibility = view == "playlist" && selectedPlaylist != null ? Visibility.Visible : Visibility.Collapsed;
        ScheduleButton.Foreground = (Brush)FindResource(selectedPlaylist?.ScheduleEnabled == true ? "Accent" : "Muted");
        if (view == "detail" && detailTracks?.Count > 0)
        {
            var t = detailTracks[0]; DetailArt.Source = (ImageSource)new ArtworkConverter().Convert(t.DisplayArtwork, typeof(ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture);
            DetailArtist.Text = string.IsNullOrEmpty(t.AlbumArtist) ? t.Artist : t.AlbumArtist;
            DetailText.Text = $"{detailTracks.Count} 曲 · {TimeSpan.FromSeconds(detailTracks.Sum(t => t.Seconds)).TotalMinutes:F0} 分" + (t.Year > 0 ? $" · {t.Year}" : "");
        }
        if (isCards)
        {
            cards = view == "artists"
                ? filtered.GroupBy(t => string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist).OrderBy(g => g.Key).Select(g => new AlbumCard { Title = g.Key, Subtitle = $"{g.Select(t => t.AlbumKey).Distinct().Count()} アルバム", Detail = $"{g.Count()} TRACKS", Artwork = g.FirstOrDefault(t => t.DisplayArtwork.Length > 0)?.DisplayArtwork ?? "", Tracks = g.ToList(), IsArtist = true }).ToList()
                : filtered.GroupBy(t => t.AlbumKey).OrderBy(g => g.First().Album).Select(g => new AlbumCard { Title = g.First().Album, Subtitle = string.IsNullOrWhiteSpace(g.First().AlbumArtist) ? g.First().Artist : g.First().AlbumArtist, Detail = $"{g.Count()} TRACKS" + (g.First().Year > 0 ? $"   /   {g.First().Year}" : ""), Artwork = g.FirstOrDefault(t => t.DisplayArtwork.Length > 0)?.DisplayArtwork ?? "", Tracks = g.OrderBy(t => t.Disc).ThenBy(t => t.Number).ThenBy(t => t.Title).ToList() }).ToList();
            cards = LibraryOrdering.Albums(cards, CurrentSort);
            visibleTracks = cards.SelectMany(c => c.Tracks).ToList();
            RenderCards();
            PageSubtitle.Text = $"{cards.Count:N0} {(view == "artists" ? "アーティスト" : "アルバム")}  ·  {filtered.Count:N0} 曲";
        }
        else
        {
            AlbumItems.ItemsSource = null; CollectionList.ItemsSource = null; TrackGrid.ItemsSource = filtered;
            PageSubtitle.Text = $"{filtered.Count:N0} 曲  ·  {TimeSpan.FromSeconds(filtered.Sum(t => t.Seconds)).TotalMinutes:N0} 分" + (view == "playlist" ? "  ·  ドラッグして曲順を変更" : view == "queue" ? "  ·  ダブルクリックで再生" : "");
            if (view == "playlist" && selectedPlaylist?.ScheduleEnabled == true) PageSubtitle.Text += "  ·  時間帯再生 ON";
        }
        EmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = SearchBox.Text.Length > 0 ? "一致する曲がありません" : "曲がありません";
        EmptyHint.Text = SearchBox.Text.Length > 0 ? "検索語や検索対象を変えてみてください" : view == "youtube" ? "「URLから取り込む」で追加した曲が並びます" : "曲やフォルダーをドラッグして追加できます";
        RefreshNav();
    }
    private string CurrentSort => view switch
    {
        "albums" or "artistAlbums" => Preferences.AlbumSort,
        "artists" => Preferences.ArtistSort,
        "playlist" => Preferences.PlaylistSort,
        "queue" => "default",
        _ => Preferences.TrackSort
    };
    private void RefreshSortSelector()
    {
        sortSync = true;
        SortSelector.Visibility = view is "queue" or "utility" ? Visibility.Collapsed : Visibility.Visible;
        var options = new List<SortOption>();
        if (view is not ("albums" or "artists" or "artistAlbums"))
            options.Add(new("default", view == "playlist" ? "マイリスト順" : "標準の順序"));
        options.AddRange(new[] { new SortOption("nameAsc", "名前順（昇順）"), new("nameDesc", "名前順（降順）"), new("addedAsc", "追加順（昇順）"), new("addedDesc", "追加順（降順）") });
        SortSelector.ItemsSource = options;
        SortSelector.SelectedValue = options.Any(o => o.Id == CurrentSort) ? CurrentSort : options[0].Id;
        sortSync = false;
    }
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || sortSync || SortSelector.SelectedItem is not SortOption option) return;
        switch (view)
        {
            case "albums": case "artistAlbums": Preferences.AlbumSort = option.Id; break;
            case "artists": Preferences.ArtistSort = option.Id; break;
            case "playlist": Preferences.PlaylistSort = option.Id; break;
            default: Preferences.TrackSort = option.Id; break;
        }
        foreach (var column in TrackGrid.Columns) column.SortDirection = null;
        RefreshView(); ScheduleSave();
    }
    private void RenderCards()
    {
        if (CollectionViewMode == "list") { AlbumItems.ItemsSource = null; CollectionList.ItemsSource = cards; if (cards.Count > 0) CollectionList.ScrollIntoView(cards[0]); return; }
        CollectionList.ItemsSource = null;
        AlbumItems.ItemsSource = cards.Chunk(albumColumns).ToList();
        if (AlbumItems.Items.Count > 0) AlbumItems.ScrollIntoView(AlbumItems.Items[0]);
    }
    private void ResizeGallery()
    {
        var width = Math.Max(180, AlbumItems.ActualWidth - 26);
        var columns = Math.Max(1, (int)(width / 200));
        Resources["AlbumSize"] = Math.Floor(width / columns) - 18;
        Resources["AlbumRowHeight"] = (double)Resources["AlbumSize"] + 100;
        if (albumColumns == columns) return;
        albumColumns = columns;
        if (ready && AlbumItems.ItemsSource != null) RenderCards();
    }
    private void Nav_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);
    private string CollectionViewMode => view == "artists" ? Preferences.ArtistViewMode : Preferences.AlbumViewMode;
    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        var mode = CollectionViewMode == "list" ? "grid" : "list";
        if (view == "artists") Preferences.ArtistViewMode = mode; else Preferences.AlbumViewMode = mode;
        RefreshView(); SaveSettings();
    }
    public void Navigate(string target)
    {
        view = target; parentView = target; detailTracks = null; selectedPlaylist = null;
        playlistSync = true; PlaylistList.SelectedItem = null; playlistSync = false; RefreshView();
    }
    private void Album_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AlbumCard card) return;
        if (view != "artistAlbums") parentView = view;
        detailTracks = card.Tracks; view = card.IsArtist ? "artistAlbums" : "detail"; RefreshView();
    }
    private void ShowArtistDetails(Track track)
    {
        static string Key(Track t) => string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist;
        var name = Key(track); new ArtistDetailsWindow(this, name, Library.Tracks.Where(t => Key(t) == name)).ShowDialog();
    }
    private void Collection_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button button || button.Tag is not AlbumCard card || card.Tracks.Count == 0) { e.Handled = true; return; }
        PopulateCollectionMenu(button.ContextMenu, card);
    }
    internal void PopulateCollectionMenu(ContextMenu menu, AlbumCard card)
    {
        var tracks = card.Tracks.OrderBy(t => t.AlbumKey).ThenBy(t => t.Disc).ThenBy(t => t.Number == 0 ? uint.MaxValue : t.Number).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        PopulateMusicMenu(menu, tracks, card);
    }
    private void Back_Click(object sender, RoutedEventArgs e) { view = parentView; detailTracks = null; RefreshView(); }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchHint != null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ready) return; searchTimer.Stop(); searchTimer.Start();
    }
    private void Search_FieldChanged(object sender, SelectionChangedEventArgs e) { if (ready) RefreshView(); }
    private List<Track> DisplayOrder() => TrackGrid.Visibility == Visibility.Visible ? TrackGrid.Items.Cast<Track>().ToList() : visibleTracks;
    private void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not Track t || Ancestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        PlayTrack(t);
    }
    internal List<Track> AlbumPlaybackOrder(Track t) => Library.Tracks.Where(track => track.AlbumKey == t.AlbumKey).OrderBy(track => track.Disc).ThenBy(track => track.Number == 0 ? uint.MaxValue : track.Number).ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    internal void PlayTrack(Track t)
    {
        if (view == "queue") { var visibleIndex = TrackGrid.Items.IndexOf(t); var original = P.Queue.Select((track, i) => (track, i)).FirstOrDefault(p => ReferenceEquals(p.track, t)); P.PlayAt(original.i); }
        else if (view == "playlist" && selectedPlaylist != null) PlayPlaylist(selectedPlaylist, t);
        else P.PlayCollection(AlbumPlaybackOrder(t), t);
    }
    public void PlayPlaylist(Playlist playlist, Track? selected = null) => P.PlayPlaylist(playlist, PlaylistTracks(playlist), selected);
    private void PlayAll_Click(object sender, RoutedEventArgs e) { if (view == "playlist" && selectedPlaylist != null && selectedPlaylist.ScheduleEnabled) { PlayPlaylist(selectedPlaylist); return; } var tracks = DisplayOrder(); if (tracks.Count > 0) { if (view == "queue") P.PlayAt(0); else P.PlayCollection(tracks); } }
    private void Schedule_Click(object sender, RoutedEventArgs e) { if (selectedPlaylist != null) EditPlaylistSchedule(selectedPlaylist); }
    private void EditPlaylistSchedule(Playlist playlist) => new PlaylistScheduleWindow(this, playlist).ShowDialog();
    public void TogglePlayback()
    {
        if (P.Current != null) { P.Toggle(); return; }
        if (view == "playlist" && selectedPlaylist?.ScheduleEnabled == true) { PlayPlaylist(selectedPlaylist); return; }
        var track = TrackGrid.SelectedItem as Track ?? DisplayOrder().FirstOrDefault();
        if (track != null) PlayTrack(track);
    }
    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void Stop_Click(object sender, RoutedEventArgs e) { P.Stop(); ScheduleSave(); }
    private void Previous_Click(object sender, RoutedEventArgs e) => P.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => P.Next();
    private void Mute_Click(object sender, RoutedEventArgs e) => P.Muted = !P.Muted;
    private void PrimaryMute_Click(object sender, RoutedEventArgs e) => P.PrimaryOutputMuted = !P.PrimaryOutputMuted;
    private void SecondaryMute_Click(object sender, RoutedEventArgs e) => P.SecondaryOutputMuted = !P.SecondaryOutputMuted;
    private void Shuffle_Click(object sender, RoutedEventArgs e) => P.Shuffle = !P.Shuffle;
    private void Repeat_Click(object sender, RoutedEventArgs e) => P.Repeat = (P.Repeat + 1) % 3;
    private void Queue_Click(object sender, RoutedEventArgs e) => Navigate("queue");
    public void ToggleFavorite(Track? track)
    {
        if (track == null) return;
        track.Favorite = !track.Favorite;
        var libraryTrack = Library.Tracks.FirstOrDefault(t => t.Id == track.Id); if (libraryTrack != null) libraryTrack.Favorite = track.Favorite;
        SaveLibrary(); if (view == "favorites") RefreshView();
    }
    private void Favorite_Click(object sender, RoutedEventArgs e) { ToggleFavorite(((Button)sender).Tag as Track); e.Handled = true; }
    private void CurrentFavorite_Click(object sender, RoutedEventArgs e) => ToggleFavorite(P.Current);
    private void CurrentAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (P.Current == null) return; parentView = "albums"; view = "detail"; SearchBox.Clear(); detailTracks = Library.Tracks.Where(t => t.AlbumKey == P.Current.AlbumKey).OrderBy(t => t.Disc).ThenBy(t => t.Number).ToList(); RefreshView();
    }
    private void RefreshPlaylists()
    {
        var id = selectedPlaylist?.Id; playlistSync = true;
        PlaylistList.ItemsSource = null; PlaylistList.ItemsSource = Library.Playlists.ToList();
        selectedPlaylist = Library.Playlists.FirstOrDefault(p => p.Id == id); PlaylistList.SelectedItem = selectedPlaylist; playlistSync = false;
    }
    private void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || playlistSync || PlaylistList.SelectedItem is not Playlist p) return;
        selectedPlaylist = p; view = "playlist"; detailTracks = null; SearchBox.Clear(); RefreshView();
    }
    private void NewPlaylist_Click(object sender, RoutedEventArgs e) => CreatePlaylist();
    private Playlist? CreatePlaylist()
    {
        var name = Dialogs.Prompt(this, "新しいマイリスト", "マイリスト名", "新しいマイリスト");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var p = new Playlist { Name = name.Trim() }; Library.Playlists.Add(p); SaveLibrary(); RefreshPlaylists(); PlaylistList.SelectedItem = p; return p;
    }
    private void RenamePlaylist(Playlist p)
    {
        var name = Dialogs.Prompt(this, "マイリストの名前を変更", "マイリスト名", p.Name);
        if (string.IsNullOrWhiteSpace(name)) return; p.Name = name.Trim(); SaveLibrary(); RefreshPlaylists(); RefreshView();
    }
    private void DeletePlaylist(Playlist p)
    {
        if (MessageBox.Show(this, $"「{p.Name}」を削除しますか？\n元の音楽ファイルは削除されません。", "マイリストを削除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Library.Playlists.Remove(p); SaveLibrary(); RefreshPlaylists(); Navigate("albums");
    }
    public void AddToPlaylist(Playlist p, IEnumerable<Track> tracks)
    {
        var count = 0; foreach (var t in tracks) if (!p.TrackIds.Contains(t.Id)) { p.TrackIds.Add(t.Id); count++; }
        SaveLibrary(); SetStatus($"「{p.Name}」に {count} 曲追加しました"); if (selectedPlaylist == p) RefreshView();
    }
    private void RemoveFromPlaylist(IEnumerable<Track> tracks)
    {
        if (selectedPlaylist == null) return; var ids = tracks.Select(t => t.Id).ToHashSet(); selectedPlaylist.TrackIds.RemoveAll(ids.Contains); SaveLibrary(); RefreshView();
    }
    private void ExportPlaylist(Playlist p)
    {
        var dialog = new SaveFileDialog { Filter = "M3U8 マイリスト|*.m3u8", FileName = DownloadService.SafeName(p.Name) + ".m3u8", InitialDirectory = Paths.Root };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllLines(dialog.FileName, new[] { "#EXTM3U" }.Concat(PlaylistTracks(p).SelectMany(t => new[] { $"#EXTINF:{(int)t.Seconds},{t.Artist.Replace('\n', ' ')} - {t.Title.Replace('\n', ' ')}", t.Path })), new System.Text.UTF8Encoding(true)); SetStatus("マイリストを書き出しました。"); } catch (Exception ex) { ReportError(ex); }
    }
    private static MenuItem Menu(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); return item; }
    private void Playlist_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject); var p = item?.DataContext as Playlist ?? PlaylistList.SelectedItem as Playlist;
        if (p == null) { e.Handled = true; return; }
        var menu = PlaylistList.ContextMenu; menu.Items.Clear();
        menu.Items.Add(Menu("再生", () => PlayPlaylist(p))); menu.Items.Add(Menu("時間帯で再生…", () => EditPlaylistSchedule(p))); menu.Items.Add(Menu("名前を変更", () => RenamePlaylist(p))); menu.Items.Add(Menu("M3U8を書き出す", () => ExportPlaylist(p))); menu.Items.Add(Menu("マイリストを削除", () => DeletePlaylist(p)));
    }
    private void ViewMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Bottom };
        if (view == "artistAlbums" && detailTracks?.Count > 0) menu.Items.Add(Menu("アーティストの詳細", () => ShowArtistDetails(detailTracks[0])));
        menu.Items.Add(Menu("表示中の曲をキューに追加", () => P.Enqueue(DisplayOrder(), false)));
        if (selectedPlaylist != null && view == "playlist")
        {
            menu.Items.Add(Menu("時間帯で再生…", () => EditPlaylistSchedule(selectedPlaylist)));
            menu.Items.Add(Menu("名前を変更", () => RenamePlaylist(selectedPlaylist))); menu.Items.Add(Menu("M3U8を書き出す", () => ExportPlaylist(selectedPlaylist))); menu.Items.Add(Menu("マイリストを削除", () => DeletePlaylist(selectedPlaylist)));
        }
        if (view == "queue") menu.Items.Add(Menu("再生中の曲以外をキューから削除", () => { for (var i = P.Queue.Count - 1; i >= 0; i--) if (i != P.QueueIndex) P.RemoveAt(i); }));
        menu.IsOpen = true;
    }
    private List<Track> SelectedTracks() => TrackGrid.SelectedItems.Cast<Track>().ToList();
    private void Track_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tracks = SelectedTracks(); if (tracks.Count == 0) { e.Handled = true; return; }
        PopulateTrackMenu(tracks);
    }
    internal void PopulateTrackMenu(List<Track> tracks)
    {
        PopulateMusicMenu(TrackGrid.ContextMenu, tracks, null);
    }
    private void PopulateMusicMenu(ContextMenu menu, List<Track> tracks, AlbumCard? collection)
    {
        menu.Items.Clear();
        menu.Items.Add(Menu("再生", () => { if (collection == null) PlayTrack(tracks[0]); else P.PlayCollection(tracks); }));
        menu.Items.Add(Menu("次に再生", () => { P.Enqueue(tracks, true); SetStatus("次に再生する曲を追加しました。"); }));
        menu.Items.Add(Menu("再生キューに追加", () => P.Enqueue(tracks, false)));
        var lists = new MenuItem { Header = "マイリストに追加" };
        foreach (var p in Library.Playlists) lists.Items.Add(Menu(p.Name, () => AddToPlaylist(p, tracks)));
        lists.Items.Add(Menu("＋ 新しいマイリスト", () => { var p = CreatePlaylist(); if (p != null) AddToPlaylist(p, tracks); })); menu.Items.Add(lists);
        menu.Items.Add(Menu("お気に入りに追加 / 解除", () => { var on = !tracks.All(t => t.Favorite); foreach (var t in tracks) t.Favorite = on; SaveLibrary(); RefreshView(); }));
        if (collection == null && view == "playlist")
        {
            menu.Items.Add(Menu("マイリストから外す", () => RemoveFromPlaylist(tracks)));
            menu.Items.Add(Menu("1つ上へ", () => MoveSelection(-1))); menu.Items.Add(Menu("1つ下へ", () => MoveSelection(1)));
        }
        if (collection == null && view == "queue") menu.Items.Add(Menu("キューから外す", () => { foreach (var t in tracks) { var n = P.Queue.ToList().IndexOf(t); P.RemoveAt(n); } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu(collection == null ? "詳細" : collection.IsArtist ? "アーティストの詳細" : "アルバムの詳細", () => {
            if (collection == null) new TrackDetailsWindow(this, tracks[0]).ShowDialog();
            else new ArtistDetailsWindow(this, collection.Title, tracks, collection.IsArtist ? "アーティスト" : "アルバム").ShowDialog();
        }));
        if (collection == null && tracks[0].HasSourceUrl) menu.Items.Add(Menu("元動画を開く", () => OpenSource(tracks[0])));
        menu.Items.Add(Menu("ライブラリから削除…", () => ConfirmRemoveTracks(tracks)));
        var locations = tracks.GroupBy(t => Path.GetDirectoryName(Path.GetFullPath(t.Path))!, StringComparer.OrdinalIgnoreCase).ToList();
        void OpenLocation(Track track) { try { FileLocation.Open(track.Path); } catch (Exception ex) { ReportError(ex); } }
        if (locations.Count == 1) menu.Items.Add(Menu("保存場所を開く", () => OpenLocation(tracks[0])));
        else
        {
            var locationsMenu = new MenuItem { Header = "保存場所を開く" };
            foreach (var group in locations) { var track = group.First(); locationsMenu.Items.Add(Menu(group.Key, () => OpenLocation(track))); }
            menu.Items.Add(locationsMenu);
        }
    }
    private void Track_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is Track t && !TrackGrid.SelectedItems.Contains(t)) TrackGrid.SelectedItem = t;
    }
    private void Track_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (view == "queue") { e.Handled = true; SetStatus("キューは再生順に表示します。"); }
    }
    private void MoveSelection(int direction)
    {
        if (selectedPlaylist == null) return;
        Preferences.PlaylistSort = "default"; ScheduleSave();
        var selected = SelectedTracks().Select(t => t.Id).ToHashSet(); var list = selectedPlaylist.TrackIds;
        var indices = direction < 0 ? Enumerable.Range(0, list.Count) : Enumerable.Range(0, list.Count).Reverse();
        foreach (var i in indices) { var j = i + direction; if (selected.Contains(list[i]) && j >= 0 && j < list.Count && !selected.Contains(list[j])) (list[i], list[j]) = (list[j], list[i]); }
        SaveLibrary(); RefreshView(); foreach (var t in visibleTracks.Where(t => selected.Contains(t.Id))) TrackGrid.SelectedItems.Add(t);
    }
    public async Task AddPathsAsync(IEnumerable<string> input, Playlist? target = null)
    {
        if (busy) { SetStatus("読み込み中です。完了してから追加してください。"); return; }
        var paths = input.ToList(); var folders = paths.Where(Directory.Exists).ToList();
        foreach (var folder in folders) if (!Preferences.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Preferences.Folders.Add(folder);
        if (folders.Count > 0) { SaveSettings(); await ScanLibraryAsync(); }
        if (exiting) return;
        busy = true;
        try
        {
            var additions = new List<Track>(); var errors = new List<string>();
            var working = new LibraryState { Tracks = Library.Tracks.ToList() };
            await Task.Run(() => {
                var known = working.Tracks.ToDictionary(t => t.Id);
                foreach (var path in paths.Where(File.Exists))
                {
                    try
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (LibraryService.IsAudio(path))
                        {
                            var id = LibraryService.TrackId(path);
                            if (!known.TryGetValue(id, out var t)) { t = LibraryService.ReadTrack(path); working.Tracks.Add(t); working.ExtraFiles.Add(path); known[id] = t; }
                            additions.Add(t);
                        }
                        else if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) { LibraryService.ImportItunes(path, working); known = working.Tracks.ToDictionary(t => t.Id); }
                        else if (Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) { LibraryService.ImportM3u(path, working); known = working.Tracks.ToDictionary(t => t.Id); }
                        else errors.Add(Path.GetFileName(path));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
                }
            }, lifetime.Token);
            var liveIds = Library.Tracks.Select(t => t.Id).ToHashSet();
            Library.Tracks.AddRange(working.Tracks.Where(t => !liveIds.Contains(t.Id)));
            Library.ExtraFiles.AddRange(working.ExtraFiles.Where(p => !Library.ExtraFiles.Contains(p, StringComparer.OrdinalIgnoreCase)));
            foreach (var imported in working.Playlists)
            {
                var existing = imported.SourceKey.Length == 0 ? null : Library.Playlists.FirstOrDefault(p => p.SourceKey == imported.SourceKey);
                if (existing == null) Library.Playlists.Add(imported); else { existing.Name = imported.Name; existing.TrackIds = imported.TrackIds; }
            }
            foreach (var folder in folders) additions.AddRange(Library.Tracks.Where(t => t.Path.StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
            if (target != null) AddToPlaylist(target, additions);
            SaveLibrary(); RefreshPlaylists(); RefreshView();
            SetStatus($"{additions.Select(t => t.Id).Distinct().Count():N0} 曲を確認しました" + (working.Playlists.Count > 0 ? $" · {working.Playlists.Count} マイリストを取り込みました" : " · 同じファイルは重複登録しません") + (errors.Count > 0 ? $" · 未対応 / 読み取り不可 {errors.Count} 件" : ""));
            if (errors.Count > 0) File.WriteAllLines(Path.Combine(Paths.Data, "import-errors.log"), errors);
        }
        catch (OperationCanceledException) { SetStatus("追加をキャンセルしました。"); }
        catch (Exception ex) { ReportError(ex); }
        finally { busy = false; }
    }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetDataPresent("LuneTracks") && e.Data.GetData("LuneTracks") is List<Track> tracks)
        {
            if (view == "playlist" && selectedPlaylist != null)
            {
                var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject); var before = (row?.Item as Track)?.Id;
                if (dragPlaylistSource == selectedPlaylist.Id) ReorderPlaylist(selectedPlaylist, tracks, before);
                else AddToPlaylist(selectedPlaylist, tracks);
            }
            else if (view == "queue") P.Enqueue(tracks, false);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths, view == "playlist" ? selectedPlaylist : null);
    }
    private async void Playlist_Drop(object sender, DragEventArgs e)
    {
        var item = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not Playlist p) return; e.Handled = true;
        if (e.Data.GetDataPresent("LuneTracks") && e.Data.GetData("LuneTracks") is List<Track> tracks) AddToPlaylist(p, tracks);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths, p);
    }
    private void Drag_Over(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("LuneTracks") ? (view == "playlist" && dragPlaylistSource == selectedPlaylist?.Id && sender == TrackGrid ? DragDropEffects.Move : DragDropEffects.Copy) : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender == TrackGrid) { var pos = e.GetPosition(TrackGrid); var scroll = FindChild<ScrollViewer>(TrackGrid); if (pos.Y < 45) scroll?.LineUp(); else if (pos.Y > TrackGrid.ActualHeight - 35) scroll?.LineDown(); }
        e.Handled = true;
    }
    private void Track_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this); dragTrack = Ancestor<Button>(e.OriginalSource as DependencyObject) == null ? Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as Track : null;
    }
    private void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragTrack == null) return;
        var point = e.GetPosition(this); if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tracks = SelectedTracks(); if (!tracks.Contains(dragTrack)) tracks = new List<Track> { dragTrack };
        var data = new DataObject(); data.SetData("LuneTracks", tracks); data.SetData(DataFormats.FileDrop, tracks.Select(t => t.Path).ToArray());
        dragTrack = null; dragPlaylistSource = view == "playlist" ? selectedPlaylist?.Id : null;
        try { DragDrop.DoDragDrop(TrackGrid, data, DragDropEffects.Copy | DragDropEffects.Move); } finally { dragPlaylistSource = null; }
    }
    public void ReorderPlaylist(Playlist p, List<Track> tracks, string? before)
    {
        Preferences.PlaylistSort = "default"; ScheduleSave();
        var selected = tracks.Select(t => t.Id).ToHashSet(); if (before != null && selected.Contains(before)) return;
        var moving = p.TrackIds.Where(selected.Contains).ToList(); p.TrackIds.RemoveAll(selected.Contains);
        var index = before == null ? p.TrackIds.Count : p.TrackIds.IndexOf(before); if (index < 0) index = p.TrackIds.Count;
        p.TrackIds.InsertRange(index, moving); SaveLibrary(); RefreshView(); SetStatus("マイリストの曲順を変更しました。");
    }
    private static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null) { if (source is T t) return t; source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); } return null;
    }
    private static T? FindChild<T>(DependencyObject source) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++) { var child = VisualTreeHelper.GetChild(source, i); if (child is T t) return t; var found = FindChild<T>(child); if (found != null) return found; } return null;
    }
    private void AddMusic_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(Menu("音楽ファイルを追加…", async () => { var d = new OpenFileDialog { Multiselect = true, Filter = "音楽ファイル|*.mp3;*.m4a;*.aac;*.flac;*.wav;*.wma;*.aif;*.aiff;*.ogg;*.opus|すべてのファイル|*.*", InitialDirectory = Preferences.Folders.FirstOrDefault(Directory.Exists) ?? Paths.Root }; if (d.ShowDialog(this) == true) await AddPathsAsync(d.FileNames, view == "playlist" ? selectedPlaylist : null); }));
        menu.Items.Add(Menu("ライブラリパスを追加…", async () => { var d = new OpenFolderDialog { Title = "ライブラリパスを選択（複数選択可）", Multiselect = true }; if (d.ShowDialog(this) == true) await AddPathsAsync(d.FolderNames, view == "playlist" ? selectedPlaylist : null); }));
        menu.Items.Add(Menu("iTunes XML / M3U を読み込む…", async () => { var d = new OpenFileDialog { Multiselect = true, Filter = "ライブラリ / マイリスト|*.xml;*.m3u;*.m3u8", InitialDirectory = Preferences.Folders.FirstOrDefault(Directory.Exists) ?? Paths.Root }; if (d.ShowDialog(this) == true) await AddPathsAsync(d.FileNames); }));
        menu.Items.Add(Menu("ライブラリを再スキャン", async () => await ScanLibraryAsync())); menu.IsOpen = true;
    }
    private void ChangeArt_Click(object sender, RoutedEventArgs e)
    {
        if (detailTracks == null) return;
        var d = new OpenFileDialog { Filter = "画像|*.jpg;*.jpeg;*.png;*.bmp" }; if (d.ShowDialog(this) != true) return;
        try { var art = LibraryService.SaveArtwork(File.ReadAllBytes(d.FileName), true); var ids = detailTracks.Select(t => t.Id).ToHashSet(); foreach (var t in Library.Tracks.Where(t => ids.Contains(t.Id))) t.Artwork = art; foreach (var t in detailTracks) t.Artwork = art; SaveLibrary(); RefreshView(); SetStatus("アルバムアートを変更しました。元のファイルは変更していません。"); } catch (Exception ex) { ReportError(ex); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(this).ShowDialog();
    private void OutputDevice_Click(object sender, RoutedEventArgs e)
    {
        ShowAudioOutputs(this);
    }
    internal void ShowAudioOutputs(Window owner)
    {
        var dialog = new Window { Owner = owner, Title = "音声の出力先", Width = 600, Height = Math.Min(730, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = Dialogs.Shell(dialog, "音声の出力先"); stack.Children.Add(new AudioOutputPanel(this));
        stack.Children.Add(Dialogs.Button("完了", dialog.Close, true)); dialog.Closed += (_, _) => SaveSettings(); dialog.ShowDialog();
    }
    private void Import_Click(object sender, RoutedEventArgs e) => new ImportWindow(this).ShowDialog();
    public async Task<Track> RegisterDownload(string path)
    {
        while (busy && !exiting) await Task.Delay(100);
        if (exiting) throw new OperationCanceledException();
        await AddPathsAsync(new[] { path });
        var track = Library.Tracks.FirstOrDefault(t => t.Id == LibraryService.TrackId(path));
        if (track != null) { track.ImportedFromYouTube = true; SaveLibrary(); }
        SearchBox.Clear(); Navigate("youtube");
        return track ?? throw new IOException("保存した曲をライブラリに登録できませんでした。再試行できます。");
    }
    internal void SaveYouTubePlaylist(string sourceKey, string name, IEnumerable<string> trackIds)
    {
        var ids = trackIds.Where(id => Library.Tracks.Any(t => t.Id == id)).Distinct().ToList();
        if (ids.Count == 0) return;
        var playlist = Library.Playlists.FirstOrDefault(p => p.SourceKey == sourceKey);
        if (playlist == null) { playlist = new Playlist { SourceKey = sourceKey }; Library.Playlists.Add(playlist); }
        playlist.Name = string.IsNullOrWhiteSpace(name) ? "YouTube 再生リスト" : name.Trim();
        // A partial retry must not remove tracks saved during a previous attempt.
        playlist.TrackIds = ids.Concat(playlist.TrackIds.Where(id => !ids.Contains(id))).Distinct().ToList();
        SaveLibrary(); RefreshPlaylists();
    }
    private void Source_Click(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is Track track) OpenSource(track); e.Handled = true; }
    private void OpenSource(Track track)
    {
        if (!track.HasSourceUrl) { SetStatus(track.SourceHint); return; }
        try { Process.Start(new ProcessStartInfo(DownloadService.ValidateUrl(track.SourceUrl)) { UseShellExecute = true }); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void ConfirmRemoveTracks(List<Track> tracks)
    {
        if (busy) { SetStatus("読み込みが完了してから削除してください。"); return; }
        if (tracks.Count == 0) return;
        var dialog = new RemoveTracksWindow(this, tracks); dialog.ShowDialog();
        if (dialog.DeleteFiles is not bool deleteFiles) return;
        var errors = RemoveLibraryTracks(tracks, deleteFiles);
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "削除できなかった曲は登録を残しました", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    internal List<string> RemoveLibraryTracks(IEnumerable<Track> tracks, bool deleteFiles)
    {
        var errors = new List<string>(); var ids = new HashSet<string>();
        if (busy) { errors.Add("読み込み中です。"); return errors; }
        var requested = tracks.Select(t => t.Id).ToHashSet();
        var targets = Library.Tracks.Where(t => requested.Contains(t.Id)).ToList();
        foreach (var track in targets)
        {
            try
            {
                if (deleteFiles) { if (P.Current?.Id == track.Id) P.ReleaseCurrentFile(); LibraryRemoval.RecycleFile(track); }
                ids.Add(track.Id);
            }
            catch (Exception ex) { errors.Add(track.Title + ": " + ex.Message); }
        }
        LibraryRemoval.Unregister(Library, ids); P.RemoveTracks(ids);
        detailTracks?.RemoveAll(t => ids.Contains(t.Id));
        SaveLibrary(); SaveSettings(); RefreshPlaylists(); RefreshView();
        SetStatus($"{ids.Count:N0} 曲を" + (deleteFiles ? "元ファイルとともに削除しました。" : "登録から外しました。元ファイルは残っています。") + (errors.Count > 0 ? $" 失敗 {errors.Count} 件" : ""));
        return errors;
    }
    private void Mini_Click(object sender, RoutedEventArgs e) => ShowMini();
    public void ShowMini()
    {
        if (exiting) return;
        startupViewChosen = true;
        mini ??= new MiniWindow(this);
        Hide(); ShowInTaskbar = false;
        mini.Show();
        if (mini.WindowState == WindowState.Minimized) mini.WindowState = WindowState.Normal;
        mini.Activate(); P.SetLowUpdateRate(false);
        // Release the gallery visual tree while only the mini player is in use.
        AlbumItems.ItemsSource = null; CollectionList.ItemsSource = null; TrackGrid.ItemsSource = null;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => GC.Collect(2, GCCollectionMode.Optimized, false)));
    }
    public void RestoreMain()
    {
        if (exiting) return;
        startupViewChosen = true;
        mini?.Hide();
        RefreshView(); ShowInTaskbar = true; Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        EnsureOnScreen();
        Activate();
        var dialog = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.Owner == this && w.IsVisible);
        if (dialog != null) dialog.Activate();
        P.SetLowUpdateRate(false);
    }
    private void EnsureOnScreen()
    {
        if (WindowState != WindowState.Normal || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var bounds)) return;
        var window = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        if (System.Windows.Forms.Screen.AllScreens.Any(s => { var overlap = System.Drawing.Rectangle.Intersect(s.WorkingArea, window); return overlap.Width >= 80 && overlap.Height >= 40; })) return;
        var work = SystemParameters.WorkArea;
        Left = work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
        Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect rect);
    public void ShowTrackPicker() { RestoreMain(); Navigate("tracks"); SearchBox.Focus(); }
    private void Title_Drag(object sender, MouseButtonEventArgs e) { if (Ancestor<Button>(e.OriginalSource as DependencyObject) != null) return; if (e.ClickCount == 2) Maximize_Click(sender, e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e) => P.SetLowUpdateRate(WindowState == WindowState.Minimized);
    private void SetupTray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Lune.ico"))!.Stream;
        var icon = new System.Drawing.Icon(stream); stream.Dispose();
        tray = new System.Windows.Forms.NotifyIcon { Text = "Lune — 音楽プレイヤー", Icon = icon, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreMain);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Luneを開く", null, (_, _) => Dispatcher.Invoke(RestoreMain));
        menu.Items.Add("ミニプレイヤー", null, (_, _) => Dispatcher.Invoke(ShowMini));
        menu.Items.Add("再生 / 一時停止", null, (_, _) => Dispatcher.Invoke(TogglePlayback));
        menu.Items.Add("次の曲", null, (_, _) => Dispatcher.Invoke(() => P.Next()));
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApp)); tray.ContextMenuStrip = menu;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (exiting) return; e.Cancel = true;
        ExitApp();
    }
    public void ExitApp()
    {
        if (exiting) return;
        try { SaveSettings(); SaveLibrary(); }
        catch (Exception ex) { try { File.AppendAllText(Path.Combine(Paths.Data, "error.log"), DateTime.Now + " Exit/save: " + ex + Environment.NewLine); } catch { } }
        finally
        {
            exiting = true;
            try { Discord.Dispose(); lifetime.Cancel(); DownloadService.StopAll(); searchTimer.Stop(); saveTimer.Stop(); P.Dispose(); }
            finally { tray?.Dispose(); UnregisterMediaKeys(); mini?.Shutdown(); Application.Current.Shutdown(); }
        }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.M) { ShowMini(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.N) { CreatePlaylist(); e.Handled = true; return; }
        if (e.OriginalSource is TextBox || Ancestor<TextBox>(e.OriginalSource as DependencyObject) != null) return;
        if (ctrl && e.Key == Key.Left) P.Previous();
        else if (ctrl && e.Key == Key.Right) P.Next();
        else if (ctrl && e.Key == Key.S) P.Stop();
        else if (e.Key == Key.Space) TogglePlayback();
        else if (e.Key == Key.Enter && TrackGrid.SelectedItem is Track t) PlayTrack(t);
        else if (e.Key == Key.Delete && view == "playlist") RemoveFromPlaylist(SelectedTracks());
        else if (e.Key == Key.Delete && TrackGrid.Visibility == Visibility.Visible) ConfirmRemoveTracks(SelectedTracks());
        else if (ctrl && e.Key == Key.Up) P.Volume += P.VolumeStep;
        else if (ctrl && e.Key == Key.Down) P.Volume -= P.VolumeStep;
        else return;
        e.Handled = true;
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc); if (!selfTest && !smoke) ApplyMediaKeys();
    }
    public void ApplyMediaKeys()
    {
        UnregisterMediaKeys(); if (!Preferences.MediaKeys || hwnd == IntPtr.Zero) return;
        for (var i = 0; i < 4; i++) if (RegisterHotKey(hwnd, 6100 + i, 0x4000, (uint)(0xB0 + i))) registeredHotkeys.Add(6100 + i);
        if (registeredHotkeys.Count < 4) SetStatus("一部のメディアキーは他のアプリが使用中です。画面内のボタンを利用できます。");
    }
    private void UnregisterMediaKeys() { foreach (var id in registeredHotkeys) UnregisterHotKey(hwnd, id); registeredHotkeys.Clear(); }
    private IntPtr WindowProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && registeredHotkeys.Contains(wParam.ToInt32()))
        {
            switch (wParam.ToInt32()) { case 6100: P.Next(); break; case 6101: P.Previous(); break; case 6102: P.Stop(); break; case 6103: TogglePlayback(); break; } handled = true;
        }
        return IntPtr.Zero;
    }
}
