using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lune;
public sealed class ImportWindow : Window
{
    private readonly MainWindow main;
    private readonly TextBox url, title;
    private readonly ComboBox artist, album;
    private readonly TextBlock albumHint;
    internal ComboBox ArtistInput => artist;
    internal ComboBox AlbumInput => album;
    private readonly TextBlock status;
    private readonly Image image;
    private readonly ComboBox format;
    private readonly Button fetch, save, setup, pick, playlist;
    private readonly TextBlock artLabel;
    internal Button CropButton { get; }
    private CancellationTokenSource? cancellation;
    private VideoInfo? info;
    private string artwork = "";
    private string originalArtwork = "";
    private Int32Rect? cropBounds;
    internal string ArtworkPath => artwork;
    internal string OriginalArtworkPath => originalArtwork;
    internal Int32Rect? ArtworkBounds => cropBounds;
    private bool running, importCompleted;
    public ImportWindow(MainWindow main)
    {
        this.main = main; Owner = main; Width = 590; Height = Math.Min(790, SystemParameters.WorkArea.Height - 32); Title = "URLから取り込む"; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Register before Shell's Escape handler so a popup closes before its window.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && (album?.IsDropDownOpen == true || artist?.IsDropDownOpen == true)) { album!.IsDropDownOpen = artist!.IsDropDownOpen = false; e.Handled = true; } };
        var stack = Dialogs.Shell(this, "YouTubeから取り込む", "保存・利用する権利がある動画のURLを指定してください。");
        url = Dialogs.Field(stack, "YouTube URL", ""); url.ToolTip = "https://www.youtube.com/watch?v=…";
        fetch = Dialogs.Button("動画の情報を取得", () => Run(Fetch), true); stack.Children.Add(fetch);
        playlist = Dialogs.Button("再生リストからまとめて取り込む…", OpenPlaylist); stack.Children.Add(playlist);
        url.TextChanged += (_, _) => { info = null; if (save != null) save.IsEnabled = false; };
        var row = new Grid { Margin = new Thickness(0, 18, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        image = new Image { Width = 80, Height = 80, Stretch = Stretch.UniformToFill, Source = (ImageSource)new ArtworkConverter().Convert("", typeof(ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture) }; row.Children.Add(image);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(right, 1); row.Children.Add(right);
        artLabel = Dialogs.Note("動画のサムネイルを自動取得します"); right.Children.Add(artLabel);
        CropButton = Dialogs.Button("切り抜き位置を調整…", EditArtwork); CropButton.IsEnabled = false; right.Children.Add(CropButton);
        pick = Dialogs.Button("画像を選ぶ…", () => { var d = new OpenFileDialog { Filter = "画像|*.jpg;*.jpeg;*.png;*.bmp" }; if (d.ShowDialog(this) == true) try { SetArtworkSource(ArtworkCrop.CacheSource(File.ReadAllBytes(d.FileName))); EditArtwork(); } catch (Exception ex) { if (status != null) status.Text = ex.Message; } }); right.Children.Add(pick); stack.Children.Add(row);
        title = Dialogs.Field(stack, "曲名"); artist = ImportInputs.Editable(stack, "アーティスト");
        artist.ToolTip = "過去の取り込みで使った名前から選択、または手入力";
        stack.Children.Add(Dialogs.Note("過去の取り込みで使ったアーティスト名を選択できます。手入力もできます。"));
        album = ImportInputs.Editable(stack, "アルバム");
        album.ToolTip = "同じアーティストの登録済みアルバムから選択、または手入力";
        albumHint = Dialogs.Note(""); albumHint.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(albumHint);
        ImportInputs.OnTextChanged(artist, (_, _) => RefreshAlbumSuggestions());
        artist.DropDownOpened += (_, _) => RefreshArtistSuggestions();
        album.DropDownOpened += (_, _) => RefreshAlbumSuggestions();
        RefreshAlbumSuggestions();
        RefreshArtistSuggestions();
        stack.Children.Add(new TextBlock { Text = "保存形式", Margin = new Thickness(0, 12, 0, 6), FontSize = 12 });
        format = new ComboBox { ItemsSource = new[] { "M4A · AAC", "MP3 · 高音質" }, SelectedIndex = 0 }; stack.Children.Add(format);
        stack.Children.Add(Dialogs.Note("取得した情報は保存前に編集できます。\n元動画のURLは音楽ファイルとライブラリに保存し、曲の「詳細」で確認できます。"));
        save = Dialogs.Button("↓  保存してライブラリに追加", () => Run(Save), true); save.IsEnabled = false; stack.Children.Add(save);
        status = Dialogs.Note("URLを入力して「動画の情報を取得」を押してください。"); status.Margin = new Thickness(0, 12, 0, 5); stack.Children.Add(status);
        var cancel = Dialogs.Button("処理をキャンセル", () => cancellation?.Cancel()); stack.Children.Add(cancel);
        setup = Dialogs.Button(DownloadService.ToolsReady ? "取り込み機能を更新" : "取り込み機能を準備（初回のみダウンロード）", () => Run(async ct => { await DownloadService.InstallToolsAsync(Progress(), ct, true); if (setup != null) setup.Content = "取り込み機能を更新"; })); stack.Children.Add(setup);
        stack.Children.Add(Dialogs.Note("保存先: " + main.Preferences.DownloadFolder));
        Closing += (_, e) => { if (running) { cancellation?.Cancel(); e.Cancel = true; status.Text = "キャンセルしています。処理が止まってから閉じてください。"; } };
    }
    private void OpenPlaylist()
    {
        var dialog = new PlaylistImportWindow(main, url.Text) { Owner = this }; dialog.ShowDialog();
        if (dialog.ImportCompleted)
        {
            importCompleted = true;
            // Playlist URLs can open this dialog from inside Run(Fetch).
            if (!running && !main.LifetimeToken.IsCancellationRequested) Close();
        }
        else { RefreshArtistSuggestions(); RefreshAlbumSuggestions(); }
    }
    private void RefreshArtistSuggestions() => ImportInputs.Suggestions(artist, main.ImportArtists.Suggestions(main.Library.Tracks, info?.Artist ?? ""));
    private void RefreshAlbumSuggestions()
    {
        var suggestions = ImportAlbumSuggestions.Find(main.Library.Tracks, artist.Text);
        ImportInputs.Suggestions(album, suggestions);
        albumHint.Text = suggestions.Count > 0 ? $"同じアーティストのアルバム {suggestions.Count} 件から選択できます。手入力もできます。"
            : "アーティストの登録済みアルバムを候補に表示します。新しい名前も手入力できます。";
    }
    private IProgress<string> Progress() => new Progress<string>(s => { status.Text = s.Length > 350 ? s[^350..] : s; });
    private async void Run(Func<CancellationToken, Task> action)
    {
        if (running || main.LifetimeToken.IsCancellationRequested) return; running = true; cancellation = CancellationTokenSource.CreateLinkedTokenSource(main.LifetimeToken); SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "処理を中止しました。通信が時間切れの場合は再試行してください。"; }
        catch (Exception ex) { status.Text = "取り込みできませんでした: " + ex.Message; }
        finally
        {
            running = false; cancellation.Dispose(); cancellation = null; SetBusy(false);
            if (importCompleted && !main.LifetimeToken.IsCancellationRequested) Close();
        }
    }
    private void SetBusy(bool value)
    {
        fetch.IsEnabled = setup.IsEnabled = pick.IsEnabled = playlist.IsEnabled = url.IsEnabled = title.IsEnabled = artist.IsEnabled = album.IsEnabled = format.IsEnabled = !value;
        save.IsEnabled = !value && info != null;
        CropButton.IsEnabled = !value && originalArtwork.Length > 0;
    }
    private async Task Fetch(CancellationToken ct)
    {
        var validated = DownloadService.ValidateUrl(url.Text);
        if (YouTubePlaylist.PlaylistId(validated).Length > 0 && YouTubePlaylist.VideoId(validated).Length == 0) { OpenPlaylist(); return; }
        info = null; ClearArtwork();
        if (!DownloadService.ToolsReady) await DownloadService.InstallToolsAsync(Progress(), ct);
        status.Text = "動画の情報を取得しています…";
        info = await DownloadService.FetchAsync(validated, ct); ApplyMetadata(info);
        if (info.Thumbnail.Length > 0)
        {
            try
            {
                var path = await DownloadService.FetchArtAsync(info.Thumbnail, ct); ct.ThrowIfCancellationRequested();
                if (path.Length > 0) { SetArtworkSource(path); EditArtwork(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { artLabel.Text = "画像を取得できませんでした。画像を選択できます。"; }
        }
        status.Text = $"情報を取得しました · {PlayerService.FormatTime(info.Seconds)}\n曲名などを確認して保存してください。";
    }
    internal void ApplyMetadata(VideoInfo video) { title.Text = video.Title; artist.Text = video.Artist; album.Text = video.Album; ImportInputs.Suggestions(artist, main.ImportArtists.Suggestions(main.Library.Tracks, video.Artist)); }
    private void ClearArtwork() { artwork = originalArtwork = ""; cropBounds = null; UpdateArtworkPreview(); }
    internal void SetArtworkSource(string path)
    {
        var bitmap = ArtworkCrop.Load(path); var selection = new CropSelection(bitmap.PixelWidth, bitmap.PixelHeight);
        var square = ArtworkCrop.Save(bitmap, selection.Bounds);
        originalArtwork = path; artwork = square; cropBounds = selection.Bounds; UpdateArtworkPreview();
    }
    private void UpdateArtworkPreview()
    {
        image.Source = (ImageSource)new ArtworkConverter().Convert(artwork, typeof(ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture);
        artLabel.Text = artwork.Length > 0 ? "正方形のアート · 切り抜き位置を変更できます" : "画像は未設定です";
        CropButton.IsEnabled = !running && originalArtwork.Length > 0;
    }
    private void EditArtwork()
    {
        if (originalArtwork.Length == 0) return;
        try
        {
            var dialog = new ArtworkCropWindow(this, originalArtwork, cropBounds); dialog.ShowDialog();
            if (dialog.ResultPath == null) return;
            artwork = dialog.ResultPath; cropBounds = dialog.ResultBounds; UpdateArtworkPreview();
        }
        catch (Exception ex) { status.Text = "切り抜きできませんでした: " + ex.Message; }
    }
    private async Task Save(CancellationToken ct)
    {
        if (info == null) return;
        status.Text = "音声を取り込んでいます…";
        var path = await DownloadService.ImportAsync(info, title.Text, artist.Text, album.Text, format.SelectedIndex == 0 ? "m4a" : "mp3", artwork, main.Preferences.DownloadFolder, Progress(), ct);
        await main.RegisterDownload(path); main.ImportArtists.Remember(info.Artist, artist.Text); RefreshArtistSuggestions(); RefreshAlbumSuggestions();
        status.Text = "保存してライブラリに追加しました。\n" + Path.GetFileName(path) + (main.ImportArtists.Warning is string warning ? "\n" + warning : ""); info = null;
        main.SetStatus(status.Text); importCompleted = true;
    }
}
