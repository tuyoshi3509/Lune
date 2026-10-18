using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

public sealed class PlaylistScheduleWindow : Window
{
    internal ObservableCollection<PlaylistTimeRange> Ranges { get; }
    internal CheckBox EnabledCheck { get; }
    internal CheckBox LoopCheck { get; }
    internal ComboBox FadeInput { get; }
    internal Button SaveButton { get; }
    internal TextBlock ValidationText { get; }
    private sealed record Choice(int Value, string Label);
    private sealed record Song(string Id, string Label);
    public PlaylistScheduleWindow(MainWindow main, Playlist playlist)
    {
        Owner = main; Title = "時間帯で再生 — " + playlist.Name; Width = 820; Height = Math.Min(710, SystemParameters.WorkArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = Dialogs.Shell(this, "時間帯で再生", playlist.Name);
        EnabledCheck = Dialogs.Check(stack, "このマイリストで時間帯再生を使う", playlist.ScheduleEnabled, _ => { });
        stack.Children.Add(Dialogs.Note("PCの現在時刻に合わせて、毎日同じ時間帯のBGMを再生します。マイリストの「すべて再生」で、現在の時間帯の曲から始めます。"));
        stack.Children.Add(Dialogs.Heading("時間帯とBGM"));
        stack.Children.Add(Dialogs.Note("開始時刻を含み、終了時刻で次の範囲に切り替わります。22時〜6時は翌朝まで、0時〜24時は終日です。時間帯の重複はできません。"));
        var songs = main.PlaylistTracks(playlist).Select(t => new Song(t.Id, t.Title + " · " + t.Artist)).ToArray();
        ValidationText = Dialogs.Note("");
        Ranges = new(playlist.TimeRanges.Select(r => r.Copy()));
        Ranges.CollectionChanged += (_, _) => ValidationText.Text = "";
        var grid = new DataGrid { ItemsSource = Ranges, AutoGenerateColumns = false, CanUserSortColumns = false, CanUserAddRows = false, IsReadOnly = false, RowHeight = 46, Height = 230, Margin = new Thickness(0, 12, 0, 8), SelectionMode = DataGridSelectionMode.Single };
        DataGridTemplateColumn SelectColumn(string label, string property, object items, string valuePath, string displayPath, double width, bool star = false)
        {
            var input = new FrameworkElementFactory(typeof(ComboBox)); input.SetValue(ItemsControl.ItemsSourceProperty, items);
            input.SetValue(ComboBox.SelectedValuePathProperty, valuePath); input.SetValue(ItemsControl.DisplayMemberPathProperty, displayPath);
            input.SetBinding(ComboBox.SelectedValueProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            input.SetBinding(ToolTipProperty, new Binding("SelectedItem." + displayPath) { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
            input.SetValue(MarginProperty, new Thickness(2));
            return new() { Header = label, Width = new DataGridLength(width, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel), CellTemplate = new DataTemplate { VisualTree = input } };
        }
        grid.Columns.Add(SelectColumn("開始", nameof(PlaylistTimeRange.StartHour), Enumerable.Range(0, 24).Select(h => new Choice(h, $"{h:00}:00")).ToArray(), "Value", "Label", 104));
        grid.Columns.Add(SelectColumn("終了", nameof(PlaylistTimeRange.EndHour), Enumerable.Range(0, 25).Select(h => new Choice(h, $"{h:00}:00")).ToArray(), "Value", "Label", 104));
        grid.Columns.Add(SelectColumn("再生する曲", nameof(PlaylistTimeRange.TrackId), songs, "Id", "Label", 1, true));
        var remove = new FrameworkElementFactory(typeof(Button)); remove.SetValue(ContentControl.ContentProperty, "削除"); remove.SetValue(MarginProperty, new Thickness(2));
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (((FrameworkElement)sender).DataContext is PlaylistTimeRange range) Ranges.Remove(range); }));
        grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 70, CellTemplate = new DataTemplate { VisualTree = remove } }); stack.Children.Add(grid);
        var add = Dialogs.Button("＋ 時間帯を追加", () => {
            var hour = Enumerable.Range(0, 24).FirstOrDefault(h => Ranges.All(r => !r.Contains(h)), -1);
            if (hour < 0) { ValidationText.Text = "すべての時間帯が設定されています。既存の範囲を調整してください。"; return; }
            var range = new PlaylistTimeRange { StartHour = hour, EndHour = hour + 1, TrackId = songs.FirstOrDefault()?.Id ?? "" };
            Ranges.Add(range); grid.ScrollIntoView(range);
        }); add.IsEnabled = songs.Length > 0; stack.Children.Add(add);
        if (songs.Length == 0) stack.Children.Add(Dialogs.Note("先にこのマイリストへ曲を追加してください。"));
        LoopCheck = Dialogs.Check(stack, "時間帯の曲が終わったら、その曲を繰り返す", playlist.ScheduleLoop, _ => { });
        stack.Children.Add(Dialogs.Note("オフの場合は、曲が終わるとマイリストの次の曲へ進みます。時間帯の切り替わりでは、再生途中でも指定した曲に移ります。"));
        stack.Children.Add(Dialogs.Heading("切り替えのフェード"));
        FadeInput = new ComboBox { ItemsSource = new[] { 1d, 2, 3, 5, 8, 10, 15, 20, 30 }.Select(s => new Choice((int)s, $"アウト {s} 秒 ＋ イン {s} 秒")).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = (int)playlist.ScheduleFadeSeconds };
        if (FadeInput.SelectedIndex < 0) FadeInput.SelectedValue = 5; stack.Children.Add(FadeInput);
        stack.Children.Add(Dialogs.Note("時間帯のない時間は通常の順序で再生します。一時停止・停止中に勝手に再生は始まりません。再開すると現在の時間帯を確認します。\n曲を選んで再生した場合はその曲を優先し、次の時間帯で切り替えます。別のマイリスト・アルバムや再生キューへ移ると、元の時間帯再生を解除します。"));
        ValidationText.Margin = new Thickness(0, 10, 0, 0); stack.Children.Add(ValidationText);
        SaveButton = Dialogs.Button("保存", () => {
            ValidationText.Text = "";
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            var error = PlaylistSchedule.Validate(Ranges, playlist.TrackIds);
            if (error != null) { ValidationText.Text = error; return; }
            if (EnabledCheck.IsChecked == true && Ranges.Count == 0) { ValidationText.Text = "時間帯を1つ以上追加してください。"; return; }
            var oldRanges = playlist.TimeRanges; var oldEnabled = playlist.ScheduleEnabled; var oldLoop = playlist.ScheduleLoop; var oldFade = playlist.ScheduleFadeSeconds;
            playlist.TimeRanges = Ranges.Select(r => r.Copy()).ToList(); playlist.ScheduleEnabled = EnabledCheck.IsChecked == true;
            playlist.ScheduleLoop = LoopCheck.IsChecked == true; playlist.ScheduleFadeSeconds = (int?)FadeInput.SelectedValue ?? 5;
            try { main.SaveLibrary(); main.RefreshView(); main.SetStatus("時間帯の設定を保存しました。"); Close(); }
            catch (Exception ex) { playlist.TimeRanges = oldRanges; playlist.ScheduleEnabled = oldEnabled; playlist.ScheduleLoop = oldLoop; playlist.ScheduleFadeSeconds = oldFade; ValidationText.Text = "保存できませんでした: " + ex.Message; }
        }, true); stack.Children.Add(SaveButton); stack.Children.Add(Dialogs.Button("キャンセル", Close));
    }
}
