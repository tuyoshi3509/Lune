using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

public sealed record ArtistListeningRow(Track Track, ListeningStats Stats);
public sealed class ArtistListeningStats : Observable, IDisposable
{
    public List<ArtistListeningRow> Rows { get; }
    public double TotalSeconds => Rows.Sum(row => row.Stats.TotalSeconds);
    public double EquivalentPlays => Rows.Sum(row => row.Stats.EquivalentPlays ?? 0);
    public int UnknownLengths => Rows.Count(row => row.Stats.TrackSeconds <= 0);
    public string TotalTimeText => $"{Math.Floor(TotalSeconds / 3600):N0}時間 {Math.Floor(TotalSeconds / 60) % 60:00}分 {Math.Floor(TotalSeconds) % 60:00}秒";
    public string TotalSecondsText => $"合計 {TotalSeconds:N1} 秒";
    public string PlayCountText => Rows.All(row => row.Stats.TrackSeconds <= 0) ? "—" : $"{EquivalentPlays:N2} 回";
    public string Note => "曲ごとの（累計再生時間 ÷ 曲の長さ）を合計" + (UnknownLengths > 0 ? $"\n長さが不明な {UnknownLengths} 曲は回数の計算から除外" : "");
    public ArtistListeningStats(IEnumerable<Track> tracks, ListeningHistory history)
    {
        Rows = tracks.DistinctBy(t => t.Id).OrderBy(t => t.Album).ThenBy(t => t.Disc).ThenBy(t => t.Number).Select(t => new ArtistListeningRow(t, history.Get(t))).ToList();
        foreach (var row in Rows) row.Stats.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ListeningStats.TotalSeconds) or nameof(ListeningStats.TrackSeconds))) return;
        foreach (var name in new[] { nameof(TotalSeconds), nameof(EquivalentPlays), nameof(TotalTimeText), nameof(TotalSecondsText), nameof(PlayCountText), nameof(Note) }) Raise(name);
    }
    public void Dispose() { foreach (var row in Rows) row.Stats.PropertyChanged -= Changed; }
}

public sealed class ArtistDetailsWindow : Window
{
    internal ArtistListeningStats Stats { get; }
    public ArtistDetailsWindow(MainWindow main, string artist, IEnumerable<Track> tracks, string kind = "アーティスト")
    {
        Owner = main; Title = kind + "の詳細 — " + artist; Width = 780; Height = Math.Min(720, SystemParameters.WorkArea.Height - 32); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        main.P.SaveListeningHistory(); Stats = new ArtistListeningStats(tracks, main.P.Listening);
        var stack = Dialogs.Shell(this, kind + "の詳細", artist);
        stack.Children.Add(Dialogs.Note($"{Stats.Rows.Count:N0} 曲 · {Stats.Rows.Select(row => row.Track.AlbumKey).Distinct().Count():N0} アルバム"));
        var metrics = new Grid { Margin = new Thickness(0, 18, 0, 0) }; metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition()); stack.Children.Add(metrics);
        void Metric(int column, string heading, string valueProperty, string noteProperty)
        {
            var panel = new StackPanel(); panel.Children.Add(Dialogs.Note(heading));
            var value = new TextBlock { FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) }; value.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); value.SetBinding(TextBlock.TextProperty, new Binding(valueProperty) { Source = Stats }); panel.Children.Add(value);
            var note = Dialogs.Note(""); note.SetBinding(TextBlock.TextProperty, new Binding(noteProperty) { Source = Stats }); panel.Children.Add(note);
            var border = new Border { Child = panel, CornerRadius = new CornerRadius(9), Padding = new Thickness(16), Margin = new Thickness(column == 1 ? 10 : 0, 0, 0, 0) }; border.SetResourceReference(Border.BackgroundProperty, "Surface"); Grid.SetColumn(border, column); metrics.Children.Add(border);
        }
        Metric(0, "累計再生時間", nameof(Stats.TotalTimeText), nameof(Stats.TotalSecondsText));
        Metric(1, "換算再生回数", nameof(Stats.PlayCountText), nameof(Stats.Note));
        stack.Children.Add(Dialogs.Heading("曲ごとの内訳"));
        var grid = new DataGrid { ItemsSource = Stats.Rows, Height = 300, RowHeight = 42, CanUserSortColumns = true };
        var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var title = new FrameworkElementFactory(typeof(MarqueeText)); title.SetBinding(MarqueeText.TextProperty, new Binding("Track.Title")); title.SetValue(MarqueeText.OnHoverProperty, true);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "曲名", CellTemplate = new DataTemplate { VisualTree = title }, SortMemberPath = "Track.Title", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "累計再生時間", Binding = new Binding("Stats.TotalTimeText"), SortMemberPath = "Stats.TotalSeconds", Width = 210 });
        grid.Columns.Add(new DataGridTextColumn { Header = "換算再生回数", Binding = new Binding("Stats.PlayCountText"), SortMemberPath = "Stats.EquivalentPlays", Width = 135 });
        stack.Children.Add(grid); stack.Children.Add(Dialogs.Note("Luneで記録した再生が対象です。現在ライブラリに登録されている曲を集計します。")); stack.Children.Add(Dialogs.Button("閉じる", Close));
        Closed += (_, _) => Stats.Dispose();
    }
}
