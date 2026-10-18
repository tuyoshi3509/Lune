using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

public sealed class MusicBookmark
{
    public string TrackId { get; set; } = "";
    public string Name { get; set; } = "";
    public double Position { get; set; }
    public override string ToString() => $"{Name} · {PlayerService.FormatTime(Position)}";
}
public sealed class MusicToolsData
{
    public List<string> Recent { get; set; } = new();
    public List<MusicBookmark> Bookmarks { get; set; } = new();
}
public partial class MainWindow
{
    private MusicToolsData? musicToolsData;
    private MusicToolsWindow? musicToolsWindow;
    private string utilityTitle = "";
    internal MusicToolsData ToolsData => musicToolsData ??= store.Load<MusicToolsData>("music-tools.json");
    private void InitializeMusicTools()
    {
        P.PropertyChanged += (_, e) => {
            if (e.PropertyName != nameof(P.IsPlaying) || !P.IsPlaying || P.Current == null) return;
            var recent = ToolsData.Recent;
            if (recent.FirstOrDefault() == P.Current.Id) return;
            recent.Remove(P.Current.Id); recent.Insert(0, P.Current.Id);
            if (recent.Count > 200) recent.RemoveRange(200, recent.Count - 200);
            SaveMusicTools();
        };
    }
    internal void SaveMusicTools()
    {
        try { store.Save("music-tools.json", ToolsData); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void MusicTools_Click(object sender, RoutedEventArgs e)
    {
        if (musicToolsWindow != null) { musicToolsWindow.Activate(); return; }
        musicToolsWindow = new MusicToolsWindow(this);
        musicToolsWindow.Closed += (_, _) => musicToolsWindow = null;
        musicToolsWindow.Show();
    }
    internal void ShowUtilityTracks(IEnumerable<Track> tracks, string title)
    {
        detailTracks = tracks.ToList(); utilityTitle = title; parentView = "tracks"; view = "utility"; selectedPlaylist = null;
        SearchBox.Clear(); RefreshView(); SetStatus(title + $" · {detailTracks.Count} 曲");
    }
    internal void SaveQueueAsPlaylist(string name)
    {
        var tracks = P.Queue.ToList();
        if (tracks.Count == 0) throw new InvalidOperationException("再生キューが空です。「再生キューに追加」で曲を追加してください。");
        Library.Playlists.Add(new Playlist { Name = name, TrackIds = tracks.Select(t => t.Id).ToList() });
        SaveLibrary(); RefreshPlaylists(); SetStatus("キューをマイリストに保存しました。");
    }
}

public sealed class MusicToolsWindow : Window
{
    public MusicToolsWindow(MainWindow main)
    {
        Owner = main; Icon = main.Icon; Title = "便利機能"; Width = 540; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = main.P;
        var panel = Dialogs.Shell(this, "便利機能");
        var status = Dialogs.Note(""); status.SetBinding(TextBlock.TextProperty, new Binding(nameof(p.UtilityStatus)) { Source = p }); panel.Children.Add(status);
        void Action(string label, Action action) => panel.Children.Add(Dialogs.Button(label, () => {
            try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lune", MessageBoxButton.OK, MessageBoxImage.Information); }
        }));
        panel.Children.Add(Dialogs.Heading("1 · スリープタイマー"));
        var minutes = Dialogs.Field(panel, "停止までの分数（1〜1440）", "30");
        Action("タイマーを開始", () => { if (!int.TryParse(minutes.Text, out var m) || m < 1 || m > 1440) throw new InvalidOperationException("1〜1440の整数を入力してください。"); p.SetSleepTimer(m); });
        Action("タイマーを解除", () => p.SetSleepTimer(0));
        panel.Children.Add(Dialogs.Heading("2 · この曲が終わったら停止"));
        var stop = new CheckBox { Content = "現在の曲の終了で停止する" };
        stop.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(p.StopAfterTrack)) { Source = p, Mode = BindingMode.TwoWay }); panel.Children.Add(stop);
        panel.Children.Add(Dialogs.Note("リピート・時間帯再生より優先。曲を変更すると解除します。"));
        panel.Children.Add(Dialogs.Heading("3 · A–B区間リピート"));
        panel.Children.Add(Dialogs.Note("再生位置をA点・B点として指定します。時間帯再生は終了します。"));
        Action("現在位置をA点にする", p.SetLoopStart); Action("現在位置をB点にして繰り返す", p.SetLoopEnd); Action("区間リピート解除", p.ClearLoop);
        panel.Children.Add(Dialogs.Heading("4 · 再生位置ブックマーク"));
        var bookmarks = new ComboBox { ItemsSource = main.ToolsData.Bookmarks.ToList(), MaxDropDownHeight = 260 };
        panel.Children.Add(bookmarks);
        Action("現在位置を保存", () => {
            var track = p.Current ?? throw new InvalidOperationException("曲を選んでください。"); var pos = p.Position;
            if (main.ToolsData.Bookmarks.Count >= 500) throw new InvalidOperationException("上限は500件です。不要なブックマークを削除してください。");
            var name = Dialogs.Prompt(this, "ブックマーク", "名前", track.Title);
            if (name == null) return;
            main.ToolsData.Bookmarks.Add(new MusicBookmark { TrackId = track.Id, Position = pos, Name = name }); main.SaveMusicTools(); bookmarks.ItemsSource = main.ToolsData.Bookmarks.ToList();
        });
        Action("選択した位置から再生", () => {
            if (bookmarks.SelectedItem is not MusicBookmark b) throw new InvalidOperationException("ブックマークを選択してください。");
            var track = main.Library.Tracks.FirstOrDefault(t => t.Id == b.TrackId) ?? throw new InvalidOperationException("この曲はライブラリから削除されています。");
            p.PlayCollection(new[] { track }, track, seek: b.Position);
        });
        Action("選択したブックマークを削除", () => { if (bookmarks.SelectedItem is MusicBookmark b) { main.ToolsData.Bookmarks.Remove(b); main.SaveMusicTools(); bookmarks.ItemsSource = main.ToolsData.Bookmarks.ToList(); } });
        panel.Children.Add(Dialogs.Heading("5 · 最近再生した曲"));
        Action("最近の200曲を表示", () => { var byId = main.Library.Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First()); main.ShowUtilityTracks(main.ToolsData.Recent.Where(byId.ContainsKey).Select(id => byId[id]), "最近再生した曲（新しい順）"); Close(); });
        panel.Children.Add(Dialogs.Heading("6 · 未再生曲"));
        Action("まだ聴いていない曲を表示", () => { main.ShowUtilityTracks(main.Library.Tracks.Where(t => !p.Listening.HasListened(t)), "未再生曲（Luneの記録に基づく）"); Close(); });
        panel.Children.Add(Dialogs.Heading("7 · おまかせアルバム"));
        Action("アルバムをランダムに選んで再生", () => {
            var albums = main.Library.Tracks.Where(t => t.ReadIssue.Length == 0).GroupBy(t => t.AlbumKey).ToList();
            if (albums.Count == 0) throw new InvalidOperationException("再生できるアルバムがありません。");
            var tracks = albums[Random.Shared.Next(albums.Count)].OrderBy(t => t.Disc).ThenBy(t => t.Number == 0 ? uint.MaxValue : t.Number).ThenBy(t => t.Title).ToList();
            p.Shuffle = false; p.PlayCollection(tracks, tracks[0]);
        });
        panel.Children.Add(Dialogs.Heading("8 · キューをマイリストに保存"));
        Action("キューを保存", () => { if (p.Queue.Count == 0) throw new InvalidOperationException("再生キューが空です。"); var name = Dialogs.Prompt(this, "キューを保存", "マイリスト名", "キュー " + DateTime.Now.ToString("MM/dd HH:mm")); if (name != null) main.SaveQueueAsPlaylist(name); });
        panel.Children.Add(Dialogs.Heading("9 · 重複候補を探す"));
        panel.Children.Add(Dialogs.Note("曲名・アーティスト名が同じ曲をまとめます。別バージョンを含む場合があります。自動削除はしません。"));
        Action("重複候補を表示", () => { main.ShowUtilityTracks(main.Library.Tracks.GroupBy(t => (t.Title.Trim().ToUpperInvariant(), t.Artist.Trim().ToUpperInvariant())).Where(g => g.Count() > 1).OrderBy(g => g.Key.Item1).SelectMany(g => g), "重複候補"); Close(); });
        panel.Children.Add(Dialogs.Heading("10 · ローカル歌詞"));
        panel.Children.Add(Dialogs.Note("音楽ファイルと同じ場所・同じ名前の.lrc / .txtを表示します。ネット接続は不要です。"));
        Action("現在の曲の歌詞を開く", () => {
            var track = p.Current ?? throw new InvalidOperationException("曲を選んでください。");
            var path = new[] { Path.ChangeExtension(track.Path, ".lrc"), Path.ChangeExtension(track.Path, ".txt") }.FirstOrDefault(File.Exists);
            if (path == null) throw new InvalidOperationException("同じ名前の.lrcまたは.txtが見つかりません。\n例：曲名.m4a → 曲名.lrc");
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("歌詞ファイルは1MB以下にしてください。");
            var window = new Window { Owner = this, Title = "歌詞 — " + track.Title, Width = 520, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var content = Dialogs.Shell(window, track.Title, track.Artist);
            content.Children.Add(new TextBox { Text = File.ReadAllText(path), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), FontSize = 16 }); window.Show();
        });
    }
}
