using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lune;
public sealed class SettingsWindow : Window
{
    internal ComboBox DeviceSelector { get; }
    internal ComboBox SecondaryDeviceSelector { get; }
    internal DiscordSettingsPanel DiscordPanel { get; }
    internal TextBox VolumeMinInput { get; } = new();
    internal TextBox VolumeMaxInput { get; } = new();
    public SettingsWindow(MainWindow main)
    {
        Owner = main; Title = "設定"; Width = 580; Height = 735; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = main.Preferences; var stack = Dialogs.Shell(this, "設定", "設定は自動で保存されます。");
        stack.Children.Add(Dialogs.Button("Lune " + AppUpdates.CurrentText + " · アップデート", () => { Close(); main.ShowUpdates(); }));
        stack.Children.Add(Dialogs.Heading("音量の範囲"));
        var volumeFields = new Grid(); volumeFields.ColumnDefinitions.Add(new ColumnDefinition()); volumeFields.ColumnDefinitions.Add(new ColumnDefinition());
        void AddVolumeField(int column, string label, TextBox input, double value)
        {
            var field = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 12, 0, 0, 0) };
            field.Children.Add(Dialogs.Note(label)); input.Text = (value * 100).ToString("0.#", CultureInfo.CurrentCulture); input.Margin = new Thickness(0, 5, 0, 0);
            field.Children.Add(input); Grid.SetColumn(field, column); volumeFields.Children.Add(field);
        }
        AddVolumeField(0, "最小（％）", VolumeMinInput, main.P.VolumeMinimum);
        AddVolumeField(1, "最大（％）", VolumeMaxInput, main.P.VolumeMaximum);
        stack.Children.Add(volumeFields);
        var rangeStatus = Dialogs.Note(""); rangeStatus.Margin = new Thickness(0, 7, 0, 0); stack.Children.Add(rangeStatus);
        rangeStatus.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("VolumeRangeLabel") { Source = main.P });
        var rangeError = Dialogs.Note(""); rangeError.Foreground = new SolidColorBrush(Color.FromRgb(240, 185, 162)); rangeError.Visibility = Visibility.Collapsed; stack.Children.Add(rangeError);
        void ApplyRange()
        {
            if (!double.TryParse(VolumeMinInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var min) ||
                !double.TryParse(VolumeMaxInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var max) ||
                !double.IsFinite(min) || !double.IsFinite(max) || min < 0 || max > 100 || min >= max)
            {
                rangeError.Text = "0〜100の数値で、最小値を最大値より小さくしてください。"; rangeError.Visibility = Visibility.Visible; return;
            }
            rangeError.Visibility = Visibility.Collapsed;
            main.P.SetVolumeRange(min / 100, max / 100); main.SaveSettings();
        }
        VolumeMinInput.LostFocus += (_, _) => ApplyRange(); VolumeMaxInput.LostFocus += (_, _) => ApplyRange();
        VolumeMinInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyRange(); e.Handled = true; } };
        VolumeMaxInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyRange(); e.Handled = true; } };
        stack.Children.Add(Dialogs.Note("入力欄を離れるかEnterで適用します。スライダー全体をこの範囲で使えます。"));
        stack.Children.Add(Dialogs.Button("初期値に戻す（0〜30％）", () => { VolumeMinInput.Text = "0"; VolumeMaxInput.Text = "30"; ApplyRange(); }));
        stack.Children.Add(Dialogs.Heading("音声の出力先"));
        var audioOutputs = new AudioOutputPanel(main); DeviceSelector = audioOutputs.PrimarySelector; SecondaryDeviceSelector = audioOutputs.SecondarySelector;
        stack.Children.Add(audioOutputs);
        DiscordPanel = new DiscordSettingsPanel(main); stack.Children.Add(DiscordPanel);
        stack.Children.Add(Dialogs.Heading("外観"));
        TextBox? hexInput = null;
        var themes = new ComboBox { ItemsSource = AppearanceThemes.All, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = p.Theme }; stack.Children.Add(themes);
        themes.SelectionChanged += (_, _) => { if (themes.SelectedItem is not AppearanceTheme theme) return; p.Theme = theme.Id; p.Accent = theme.Accent; if (hexInput != null) hexInput.Text = p.Accent; main.ApplyAppearance(); };
        stack.Children.Add(Dialogs.Note("20種類のテーマ。アクセントカラーは個別に変更できます。"));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var color in new[] { "#CBEB9B", "#99CFFF", "#C9B5F0", "#F0B9A2", "#E8D68D", "#F3F1E8" })
        {
            var b = new Button { Width = 45, Height = 32, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(0, 0, 10, 0), ToolTip = color };
            b.Click += (_, _) => { p.Accent = color; if (hexInput != null) hexInput.Text = color; main.ApplyAppearance(); }; swatches.Children.Add(b);
        }
        stack.Children.Add(swatches);
        var hex = Dialogs.Field(stack, "アクセントカラー（#RRGGBB）", p.Accent);
        hexInput = hex;
        hex.LostFocus += (_, _) => { try { var c = (Color)ColorConverter.ConvertFromString(hex.Text); p.Accent = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; main.ApplyAppearance(); } catch { hex.Text = p.Accent; } };
        var alpha = Dialogs.Note($"不透明度 {p.Opacity:P0}"); alpha.Margin = new Thickness(0, 18, 0, 2); stack.Children.Add(alpha);
        var opacity = new Slider { Minimum = 0.3, Maximum = 1, Value = p.Opacity, SmallChange = 0.05 }; stack.Children.Add(opacity);
        opacity.ValueChanged += (_, _) => { p.Opacity = opacity.Value; alpha.Text = $"不透明度 {p.Opacity:P0}"; main.ApplyAppearance(); };
        Dialogs.Check(stack, "ミニプレイヤーを常に手前に表示", p.MiniTopmost, b => { p.MiniTopmost = b; main.ApplyAppearance(); });
        Dialogs.Check(stack, "次回からミニプレイヤーで起動（標準は通常画面）", p.StartMini, b => p.StartMini = b);
        stack.Children.Add(Dialogs.Heading("再生・動作"));
        Dialogs.Check(stack, "再生操作の背景にイコライザを表示", p.SpectrumEnabled, enabled => { p.SpectrumEnabled = enabled; main.P.SpectrumEnabled = enabled; main.SaveSettings(); });
        stack.Children.Add(Dialogs.Note("初期値はオフです。オンの場合も、表示中・再生中だけ描画します。"));
        Dialogs.Check(stack, "キーボードのメディアキーで操作", p.MediaKeys, b => { p.MediaKeys = b; main.ApplyMediaKeys(); });
        stack.Children.Add(Dialogs.Note("通常画面・ミニ画面の×、Alt+F4でアプリを終了します。"));
        stack.Children.Add(Dialogs.Note("Space 再生 / 一時停止　Ctrl+← / → 前 / 次\nCtrl+S 停止　Ctrl+↑ / ↓ 音量　Ctrl+M ミニ画面\nCtrl+F 検索　Ctrl+N マイリスト作成\nゲーム中はミニ画面または通知領域で動かせます。"));
        stack.Children.Add(Dialogs.Heading("ライブラリパス"));
        var folders = new ListBox { Height = 115, SelectionMode = SelectionMode.Extended }; void Refresh() => folders.ItemsSource = p.Folders.ToList(); Refresh(); stack.Children.Add(folders);
        var pathLabel = new FrameworkElementFactory(typeof(TextBlock));
        pathLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        pathLabel.SetBinding(ToolTipProperty, new System.Windows.Data.Binding());
        pathLabel.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        folders.ItemTemplate = new DataTemplate { VisualTree = pathLabel };
        stack.Children.Add(Dialogs.Note("複数のパスを登録できます。Ctrl / Shiftで複数選択できます。"));
        stack.Children.Add(Dialogs.Button("＋ ライブラリパスを追加…", async () => { var d = new OpenFolderDialog { Title = "ライブラリパスを追加（複数選択可）", Multiselect = true }; if (d.ShowDialog(this) == true) { await main.AddPathsAsync(d.FolderNames); Refresh(); } }));
        stack.Children.Add(Dialogs.Button("選択したパスをスキャン対象から外す", () => { foreach (var folder in folders.SelectedItems.Cast<string>().ToList()) p.Folders.Remove(folder); Refresh(); main.SaveSettings(); }));
        stack.Children.Add(Dialogs.Button("ライブラリを再スキャン", async () => await main.ScanLibraryAsync()));
        stack.Children.Add(Dialogs.Button("読み込みをキャンセル", main.CancelScan));
        stack.Children.Add(Dialogs.Button("読み取り注意の一覧を開く", () => { var path = Path.Combine(Paths.Data, "read-issues.tsv"); if (File.Exists(path)) { var start = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(path); System.Diagnostics.Process.Start(start); } else main.SetStatus("再スキャンすると読み取り結果が保存されます。"); }));
        stack.Children.Add(Dialogs.Note($"登録済み {main.Library.Tracks.Count:N0} 曲 · 保護付き {main.Library.ProtectedFiles:N0} 件\nフォルダーの除外は次回以降のスキャン対象だけを変更します。登録済みの曲と元ファイルは残ります。\n保護付きM4Pは対象外。iTunesのマイリストはXMLを書き出して「音楽を追加」から取り込めます。"));
        stack.Children.Add(Dialogs.Heading("URL取り込みの保存先"));
        var download = new TextBox { Text = p.DownloadFolder, IsReadOnly = true, TextWrapping = TextWrapping.Wrap }; stack.Children.Add(download);
        stack.Children.Add(Dialogs.Button("保存先を変更", () => { var d = new OpenFolderDialog { Title = "取り込んだ音楽の保存先" }; if (d.ShowDialog(this) == true) { p.DownloadFolder = d.FolderName; download.Text = p.DownloadFolder; } }));
        stack.Children.Add(Dialogs.Heading("データ"));
        stack.Children.Add(Dialogs.Note("ライブラリ・マイリスト・設定はアプリの data フォルダーに保存されます。バックアップは data フォルダーをコピーしてください。\n\n音楽ファイルはその場で参照します。フォルダーやファイルを通常画面へドラッグすると追加できます。"));
        stack.Children.Add(Dialogs.Button("完了", Close, true));
        Closed += (_, _) => main.SaveSettings();
    }
}
