using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lune;

public sealed class PlaylistImportWindow : Window
{
    private readonly MainWindow main;
    private readonly TextBox url, title, playlistName;
    private readonly ComboBox artist, album, format;
    private readonly CheckBox skipExisting, createPlaylist;
    private readonly TextBlock status, count, artNote;
    private readonly Image art;
    private readonly Grid editor;
    private readonly Button load, start, details, crop, pick;
    private readonly List<UIElement> mutable = new();
    private CancellationTokenSource? cancellation;
    private YouTubePlaylistInfo? playlist;
    private bool running, syncing, updatingList;
    internal bool ImportCompleted { get; private set; }
    internal DataGrid TrackList { get; }
    internal ComboBox ArtistInput => artist;
    internal ComboBox AlbumInput => album;
    internal TextBox TitleInput => title;
    internal CheckBox SkipExisting => skipExisting;
    internal IReadOnlyList<PlaylistImportItem> Items => playlist?.Items ?? Array.Empty<PlaylistImportItem>();
    private PlaylistImportItem? Current => TrackList.SelectedItem as PlaylistImportItem;

    public PlaylistImportWindow(MainWindow main, string initialUrl = "")
    {
        this.main = main; Owner = main; Width = 1080; Height = Math.Min(920, SystemParameters.WorkArea.Height - 32); Title = "YouTube再生リストを取り込む"; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && (artist?.IsDropDownOpen == true || album?.IsDropDownOpen == true)) { artist!.IsDropDownOpen = album!.IsDropDownOpen = false; e.Handled = true; } };
        var stack = Dialogs.Shell(this, "再生リストからまとめて取り込む", "YouTubeの再生リストURLを指定し、取り込む曲にチェックを付けてください。");
        url = Dialogs.Field(stack, "再生リストURL", initialUrl);
        load = Dialogs.Button("再生リストの曲を取得", () => Run(Load), true); stack.Children.Add(load);
        count = Dialogs.Note("公開・限定公開の再生リストに対応します。保存・利用する権利がある動画に使用してください。"); count.Margin = new Thickness(0, 12, 0, 4); stack.Children.Add(count);
        var actions = new WrapPanel(); stack.Children.Add(actions);
        var all = Dialogs.Button("すべて選択", () => SelectAll(true)); all.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(all);
        var none = Dialogs.Button("すべて解除", () => SelectAll(false)); none.Margin = new Thickness(0, 0, 16, 0); actions.Children.Add(none);
        skipExisting = Dialogs.Check(actions, "登録済みの動画は取り込まない", true, _ => RefreshExisting());
        TrackList = new DataGrid { Height = 260, RowHeight = 42, SelectionMode = DataGridSelectionMode.Single, CanUserSortColumns = false, Margin = new Thickness(0, 8, 0, 0) };
        var check = new FrameworkElementFactory(typeof(CheckBox)); check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(PlaylistImportItem.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); check.SetBinding(IsEnabledProperty, new Binding(nameof(PlaylistImportItem.CanSelect))); check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        TrackList.Columns.Add(new DataGridTemplateColumn { Header = "取込", Width = 60, CellTemplate = new DataTemplate { VisualTree = check } });
        void Column(string header, string path, double width, bool star = false) {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(path)); text.SetBinding(ToolTipProperty, new Binding(path)); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            TrackList.Columns.Add(new DataGridTemplateColumn { Header = header, Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel), CellTemplate = new DataTemplate { VisualTree = text } });
        }
        Column("#", nameof(PlaylistImportItem.Index), 48); Column("曲名", nameof(PlaylistImportItem.Title), 2, true); Column("アーティスト", nameof(PlaylistImportItem.Artist), 1, true); Column("アルバム", nameof(PlaylistImportItem.Album), 1, true); Column("状態", nameof(PlaylistImportItem.State), 200);
        stack.Children.Add(TrackList); TrackList.SelectionChanged += (_, _) => SyncEditor();
        stack.Children.Add(Dialogs.Heading("選択中の曲を編集"));
        editor = new Grid(); for (var i = 0; i < 3; i++) editor.ColumnDefinitions.Add(new ColumnDefinition()); stack.Children.Add(editor);
        StackPanel Cell(int column) { var cell = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0) }; Grid.SetColumn(cell, column); editor.Children.Add(cell); return cell; }
        var left = Cell(0); var middle = Cell(1); var right = Cell(2);
        title = Dialogs.Field(left, "曲名"); artist = ImportInputs.Editable(middle, "アーティスト"); album = ImportInputs.Editable(right, "アルバム");
        middle.Children.Add(Dialogs.Button("チェックした曲へアーティストを反映", () => ApplyArtistToSelected(artist.Text)));
        right.Children.Add(Dialogs.Button("チェックした曲へアルバムを反映", () => ApplyAlbumToSelected(album.Text)));
        title.TextChanged += (_, _) => { if (!syncing && Current is { } item) item.Title = title.Text; };
        ImportInputs.OnTextChanged(artist, (_, _) => { if (!syncing && Current is { } item) { item.Artist = artist.Text; RefreshAlbumSuggestions(); } });
        ImportInputs.OnTextChanged(album, (_, _) => { if (!syncing && Current is { } item) item.Album = album.Text; });
        artist.DropDownOpened += (_, _) => RefreshArtistSuggestions(); album.DropDownOpened += (_, _) => RefreshAlbumSuggestions();
        var artwork = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; stack.Children.Add(artwork);
        art = new Image { Width = 68, Height = 68, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 14, 0) }; DockPanel.SetDock(art, Dock.Left); artwork.Children.Add(art);
        var artButtons = new WrapPanel(); artwork.Children.Add(artButtons);
        details = Dialogs.Button("画像・詳細を取得", () => Run(async ct => { if (Current is { } item) { await PrepareItem(item, ct); SyncEditor(); if (status != null) status.Text = "情報を取得しました。編集した名前は保持しています。" + item.ArtworkWarning; } })); artButtons.Children.Add(details);
        crop = Dialogs.Button("切り抜き位置を調整…", Crop); crop.Margin = new Thickness(8, 7, 0, 0); artButtons.Children.Add(crop);
        pick = Dialogs.Button("画像を選ぶ…", Pick); pick.Margin = new Thickness(8, 7, 0, 0); artButtons.Children.Add(pick);
        artNote = Dialogs.Note("画像は各動画から取得して中央を正方形に切り抜きます。曲を選び、画像を取得してから切り抜き位置を変更できます。"); stack.Children.Add(artNote);
        createPlaylist = Dialogs.Check(stack, "Luneにもマイリストを作成（登録済みの曲も含めて再生リストの順番に並べます）", true, _ => UpdateControls());
        playlistName = Dialogs.Field(stack, "マイリスト名");
        stack.Children.Add(new TextBlock { Text = "保存形式", Margin = new Thickness(0, 12, 0, 6) }); format = new ComboBox { ItemsSource = new[] { "M4A · AAC", "MP3 · 高音質" }, SelectedIndex = 0 }; stack.Children.Add(format);
        start = Dialogs.Button("チェックした曲をまとめて取り込む", () => Run(Save), true); stack.Children.Add(start);
        status = Dialogs.Note("曲名・アーティスト・アルバムは曲を選んで編集できます。元動画のURLも曲ごとに保存します。"); status.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(status);
        stack.Children.Add(Dialogs.Button("処理を中止", () => cancellation?.Cancel())); stack.Children.Add(Dialogs.Note("保存先: " + main.Preferences.DownloadFolder));
        mutable.AddRange(new UIElement[] { url, load, actions, TrackList, format, createPlaylist });
        url.TextChanged += (_, _) => { if (!running) { playlist = null; TrackList.ItemsSource = null; SyncEditor(); UpdateCount(); } };
        Closing += (_, e) => { if (running) { cancellation?.Cancel(); e.Cancel = true; status.Text = "処理を中止しています。保存済みの曲は残ります。"; } };
        SyncEditor(); UpdateControls();
    }
    private async void Run(Func<CancellationToken, Task> work)
    {
        if (running || main.LifetimeToken.IsCancellationRequested) return;
        running = true; cancellation = CancellationTokenSource.CreateLinkedTokenSource(main.LifetimeToken); UpdateControls();
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "処理を中止しました。保存済みの曲は残っています。"; }
        catch (Exception ex) { status.Text = "取り込みできませんでした: " + ex.Message; }
        finally
        {
            running = false; cancellation.Dispose(); cancellation = null; UpdateControls();
            if (ImportCompleted && !main.LifetimeToken.IsCancellationRequested) Close();
        }
    }
    private IProgress<string> Progress(string prefix = "") => new Progress<string>(s => status.Text = prefix + (s.Length > 300 ? s[^300..] : s));
    private async Task Load(CancellationToken ct)
    {
        var canonical = YouTubePlaylist.PlaylistUrl(url.Text);
        if (!DownloadService.ToolsReady) await DownloadService.InstallToolsAsync(Progress(), ct);
        status.Text = "再生リストの曲一覧を取得しています…";
        SetPlaylist(await DownloadService.FetchPlaylistAsync(canonical, ct));
        status.Text = "取り込む曲を選び、必要に応じて名前や画像を編集してください。";
    }
    internal void SetPlaylist(YouTubePlaylistInfo info)
    {
        playlist = info; playlistName.Text = info.Title;
        foreach (var item in info.Items) item.PropertyChanged += (_, e) => { if (!updatingList && e.PropertyName == nameof(PlaylistImportItem.Selected)) UpdateCount(); };
        RefreshExisting(); TrackList.ItemsSource = info.Items; TrackList.SelectedItem = info.Items.FirstOrDefault(i => i.CanSelect) ?? info.Items.FirstOrDefault(); UpdateCount();
    }
    internal void RefreshExisting()
    {
        var existing = main.Library.Tracks.Where(t => t.HasSourceUrl && File.Exists(t.Path)).Select(t => (Id: YouTubePlaylist.VideoId(t.SourceUrl), Track: t)).Where(t => t.Id.Length > 0).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Track.Id);
        updatingList = true;
        try { foreach (var item in Items) { item.ExistingTrackId = existing.GetValueOrDefault(YouTubePlaylist.VideoId(item.Info.Url), ""); item.RefreshAvailability(skipExisting.IsChecked == true); } }
        finally { updatingList = false; }
        UpdateCount(); SyncEditor();
    }
    internal void SelectAll(bool selected)
    {
        updatingList = true;
        try { foreach (var item in Items) item.Selected = selected; }
        finally { updatingList = false; }
        UpdateCount();
    }
    private void UpdateCount() { count.Text = playlist == null ? "再生リストURLを入力して曲を取得してください。" : $"{playlist.Title} · {Items.Count:N0} 件 · 取込対象 {Items.Count(i => i.Selected && i.CanSelect):N0} 件 · 登録済み {Items.Count(i => i.ExistingTrackId.Length > 0):N0} 件"; UpdateControls(); }
    private void UpdateControls()
    {
        foreach (var control in mutable) control.IsEnabled = !running;
        if (editor != null) editor.IsEnabled = !running && Current?.CanSelect == true;
        if (details != null) details.IsEnabled = !running && Current?.CanSelect == true;
        if (pick != null) pick.IsEnabled = !running && Current?.CanSelect == true;
        if (crop != null) crop.IsEnabled = !running && Current?.CanSelect == true && Current.OriginalArtwork.Length > 0;
        if (playlistName != null) playlistName.IsEnabled = !running && createPlaylist.IsChecked == true;
        if (start != null) start.IsEnabled = !running && playlist != null && (Items.Any(i => i.Selected && i.CanSelect) || (createPlaylist.IsChecked == true && Items.Any(i => i.ExistingTrackId.Length > 0)));
    }
    private void SyncEditor()
    {
        if (editor == null) return;
        syncing = true;
        try { title.Text = Current?.Title ?? ""; artist.Text = Current?.Artist ?? ""; album.Text = Current?.Album ?? ""; RefreshArtistSuggestions(); RefreshAlbumSuggestions(); }
        finally { syncing = false; }
        art.Source = (ImageSource)new ArtworkConverter().Convert(Current?.Artwork ?? "", typeof(ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture); UpdateControls();
    }
    private void RefreshArtistSuggestions() { var previous = syncing; syncing = true; try { ImportInputs.Suggestions(artist, main.ImportArtists.Suggestions(main.Library.Tracks, Current?.Info.Artist ?? "")); } finally { syncing = previous; } }
    private void RefreshAlbumSuggestions() { var previous = syncing; syncing = true; try { ImportInputs.Suggestions(album, ImportAlbumSuggestions.Find(main.Library.Tracks, artist.Text)); } finally { syncing = previous; } }
    internal void ApplyArtistToSelected(string value) { foreach (var item in Items.Where(i => i.Selected && i.CanSelect)) item.Artist = value; SyncEditor(); }
    internal void ApplyAlbumToSelected(string value) { foreach (var item in Items.Where(i => i.Selected && i.CanSelect)) item.Album = value; SyncEditor(); }
    internal static void SetArtwork(PlaylistImportItem item, string source)
    {
        var bitmap = ArtworkCrop.Load(source); var selection = new CropSelection(bitmap.PixelWidth, bitmap.PixelHeight);
        item.Artwork = ArtworkCrop.Save(bitmap, selection.Bounds); item.OriginalArtwork = source; item.CropBounds = selection.Bounds; item.ArtworkWarning = "";
    }
    private async Task PrepareItem(PlaylistImportItem item, CancellationToken ct)
    {
        if (!item.DetailsLoaded) item.ApplyMetadata(await DownloadService.FetchAsync(item.Info.Url, ct));
        if (item.OriginalArtwork.Length == 0 && item.Info.Thumbnail.Length > 0) {
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20)); var path = await DownloadService.FetchArtAsync(item.Info.Thumbnail, timeout.Token); if (path.Length > 0) SetArtwork(item, path); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { item.ArtworkWarning = "画像を取得できませんでした。画像は手動で追加できます。 " + ex.Message; }
        }
        ct.ThrowIfCancellationRequested();
    }
    private void Crop()
    {
        if (Current is not { } item || item.OriginalArtwork.Length == 0) return;
        try { var dialog = new ArtworkCropWindow(this, item.OriginalArtwork, item.CropBounds); dialog.ShowDialog(); if (dialog.ResultPath != null) { item.Artwork = dialog.ResultPath; item.CropBounds = dialog.ResultBounds; } SyncEditor(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void Pick()
    {
        if (Current is not { } item) return;
        var dialog = new OpenFileDialog { Filter = "画像|*.jpg;*.jpeg;*.png;*.bmp" };
        try { if (dialog.ShowDialog(this) == true) { SetArtwork(item, ArtworkCrop.CacheSource(File.ReadAllBytes(dialog.FileName))); SyncEditor(); Crop(); } }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    internal void SavePlaylist()
    {
        if (playlist == null || createPlaylist.IsChecked != true) return;
        main.SaveYouTubePlaylist("youtube:" + playlist.Id, playlistName.Text, Items.Select(i => i.ImportedTrackId.Length > 0 ? i.ImportedTrackId : i.ExistingTrackId).Where(id => id.Length > 0));
    }
    private async Task Save(CancellationToken ct)
    {
        RefreshExisting(); var selected = Items.Where(i => i.Selected && i.CanSelect).ToList(); var done = 0;
        var result = await BatchImportRunner.RunAsync(selected, async (item, token) => {
            TrackList.SelectedItem = item; TrackList.ScrollIntoView(item);
            var prefix = $"{++done}/{selected.Count} · {item.Title}\n"; status.Text = prefix + "曲の情報を取得しています…";
            if (item.SavedPath.Length == 0 || !File.Exists(item.SavedPath)) {
                await PrepareItem(item, token); SyncEditor();
                item.SavedPath = await DownloadService.ImportAsync(item.Info, item.Title, item.Artist, item.Album, format.SelectedIndex == 0 ? "m4a" : "mp3", item.Artwork, main.Preferences.DownloadFolder, Progress(prefix), token);
            }
            var track = await main.RegisterDownload(item.SavedPath); main.ImportArtists.Remember(item.Info.Artist, item.Artist); return track;
        }, _ => { SavePlaylist(); UpdateCount(); }, ct);
        SavePlaylist(); UpdateCount();
        status.Text = $"{(result.Cancelled ? "中止しました" : "処理が完了しました")} · 登録 {result.Imported} 曲 · 失敗 {result.Failed} 曲\n保存済みの曲はライブラリに残ります。失敗・未処理の曲はチェックを残して再試行できます。" + (main.ImportArtists.Warning is string warning ? "\n" + warning : "");
        ImportCompleted = !result.Cancelled && result.Failed == 0 && !ct.IsCancellationRequested;
        if (ImportCompleted) main.SetStatus($"YouTube再生リストの登録が完了しました · 新規 {result.Imported} 曲" + (main.ImportArtists.Warning is string savedWarning ? "\n" + savedWarning : ""));
    }
}
