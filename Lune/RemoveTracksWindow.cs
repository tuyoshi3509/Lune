using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Lune;

internal sealed class RemoveTracksWindow : Window
{
    internal bool? DeleteFiles { get; private set; }
    internal RemoveTracksWindow(MainWindow owner, IReadOnlyList<Track> tracks)
    {
        Owner = owner; Title = "曲を削除"; Width = 570; Height = 545; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = Dialogs.Shell(this, $"{tracks.Count:N0} 曲を削除", "マイリストと再生キューからも外れます。");
        stack.Children.Add(new TextBox { Text = string.Join("\n\n", tracks.Select(t => t.Title + "\n" + t.Path)), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 125, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 16, 0, 12) });
        stack.Children.Add(Dialogs.Note("登録のみ解除：音楽ファイルを残して一覧から外します。ライブラリパス内にある曲は再スキャンで戻ります。ファイルを直接追加して再登録することもできます。"));
        stack.Children.Add(Dialogs.Button("登録のみ解除", () => { DeleteFiles = false; Close(); }, true));
        stack.Children.Add(Dialogs.Note("元ファイルも削除：ファイルをごみ箱へ移動します。他のプレイヤーからも、この保存場所では再生できなくなります。"));
        stack.Children.Add(Dialogs.Button("元ファイルも削除（ごみ箱へ）", () => { DeleteFiles = true; Close(); }));
        stack.Children.Add(Dialogs.Button("キャンセル", Close));
    }
}
