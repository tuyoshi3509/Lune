using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Lune;

public sealed class TrackDetailsWindow : Window
{
    public TrackDetailsWindow(MainWindow main, Track track)
    {
        Owner = main; Icon = main.Icon; Title = "詳細 — " + track.Title;
        Width = 590; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        main.P.SaveListeningHistory();
        var stats = main.P.Listening.Get(track);
        var stack = Dialogs.Shell(this, "詳細", track.Title);
        stack.Children.Add(Dialogs.Heading("再生記録"));
        var panel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        void Metric(int column, string label, string valueProperty, string noteProperty)
        {
            var content = new StackPanel(); content.Children.Add(Dialogs.Note(label));
            var value = new TextBlock { FontSize = column == 0 ? 20 : 27, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            value.SetBinding(TextBlock.TextProperty, new Binding(valueProperty) { Source = stats }); content.Children.Add(value);
            var note = Dialogs.Note(""); note.SetBinding(TextBlock.TextProperty, new Binding(noteProperty) { Source = stats }); content.Children.Add(note);
            var border = new Border { Child = content, CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
            border.SetResourceReference(Border.BackgroundProperty, "Surface"); Grid.SetColumn(border, column); panel.Children.Add(border);
        }
        Metric(0, "累計再生時間", nameof(ListeningStats.TotalTimeText), nameof(ListeningStats.TotalSecondsText));
        Metric(1, "換算再生回数", nameof(ListeningStats.PlayCountText), nameof(ListeningStats.FormulaText));
        stack.Children.Add(panel);
        stack.Children.Add(Dialogs.Note("例：曲の半分を2回聴くと、換算で1.00回です。\n一時停止中やシークで飛ばした時間は含みません。\n記録はこの機能を追加した後のLuneでの再生が対象です。"));
        if (track.SourceUrl.Length > 0)
        {
            stack.Children.Add(Dialogs.Heading("元動画のURL"));
            stack.Children.Add(new TextBox { Text = track.SourceUrl, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.Children.Add(Dialogs.Button("URLをコピー", () => Clipboard.SetText(track.SourceUrl)));
            var open = Dialogs.Button("元動画を開く", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadService.ValidateUrl(track.SourceUrl)) { UseShellExecute = true }));
            Grid.SetColumn(open, 1); open.Margin = new Thickness(8, 7, 0, 0); actions.Children.Add(open); stack.Children.Add(actions);
        }
        else if (track.IsYouTube) { stack.Children.Add(Dialogs.Heading("元動画のURL")); stack.Children.Add(Dialogs.Note(track.SourceHint)); }
        stack.Children.Add(Dialogs.Heading("曲の情報"));
        if (track.ReadIssue.Length > 0) stack.Children.Add(Dialogs.Note("読み取り注意: " + track.ReadIssue));
        var added = track.AddedUtcTicks > 0 ? new DateTime(track.AddedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "準備中";
        foreach (var pair in new[] { ("曲名", track.Title), ("アーティスト", track.Artist), ("アルバム", track.Album), ("アルバムアーティスト", track.AlbumArtist), ("ジャンル", track.Genre), ("時間 / 形式", track.Duration + " / " + track.Format), (track.AddedDateEstimated ? "追加日時（既存曲の推定）" : "追加日時", added), ("ファイル", track.Path) })
        {
            stack.Children.Add(new TextBlock { Text = pair.Item1, Margin = new Thickness(0, 12, 0, 4), FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Muted") });
            stack.Children.Add(new TextBox { Text = pair.Item2, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(8) });
        }
        stack.Children.Add(Dialogs.Button("閉じる", Close, true));
    }
}
