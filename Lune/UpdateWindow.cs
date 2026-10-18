using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

public partial class MainWindow
{
    public AppUpdates Updates { get; } = new();
    private UpdateWindow? updateWindow;
    private void Updates_Click(object sender, RoutedEventArgs e) => ShowUpdates();
    internal void ShowUpdates()
    {
        if (updateWindow != null) { updateWindow.Activate(); return; }
        updateWindow = new UpdateWindow(this); updateWindow.Closed += (_, _) => updateWindow = null; updateWindow.Show();
    }
    private async Task InitializeUpdates()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--update-receipt");
        if (index >= 0 && index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "N", out _))
        {
            RestoreMain();
            var stage = Path.Combine(Paths.Data, "updates", args[index + 1]);
            if (Directory.Exists(stage))
                File.WriteAllText(Path.Combine(stage, "ack.json"), JsonSerializer.Serialize(new { Version = AppUpdates.CurrentText, ProcessId = Environment.ProcessId }));
            SetStatus($"Lune {AppUpdates.CurrentText} に更新しました。");
        }
        if (args.Any(a => a.EndsWith("-test") || a is "--smoke" or "--perf")) return;
        if (Preferences.CheckUpdatesOnStartup) await Updates.CheckAsync(false, LifetimeToken);
        if (Updates.HasUpdate) SetStatus($"Lune {Updates.Available!.Version} が公開されています。左下の「更新あり」から更新できます。");
    }
    internal void ApplyPreparedUpdate(string manifest)
    {
        // Abort before exiting if user data cannot be saved.
        SaveSettings(); SaveLibrary(); P.SaveListeningHistory();
        AppUpdates.StartHelper(manifest); ExitApp();
    }
}

public sealed class UpdateWindow : Window
{
    internal Button InstallButton { get; private set; } = null!;
    private readonly CancellationTokenSource cancellation;
    public UpdateWindow(MainWindow main)
    {
        Owner = main; Icon = main.Icon; Title = "アップデート"; Width = 540; Height = 590;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(main.LifetimeToken);
        var updater = main.Updates;
        var panel = Dialogs.Shell(this, "アップデート", "現在のバージョン " + AppUpdates.CurrentText);
        Dialogs.Check(panel, "起動時に更新を確認する（最大1日1回）", main.Preferences.CheckUpdatesOnStartup,
            value => { main.Preferences.CheckUpdatesOnStartup = value; main.SaveSettings(); });
        var state = Dialogs.Note(""); state.Margin = new Thickness(0, 16, 0, 8);
        state.SetBinding(TextBlock.TextProperty, new Binding(nameof(updater.Status)) { Source = updater }); panel.Children.Add(state);
        var notes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(notes);
        var check = Dialogs.Button("今すぐ確認", () => { });
        check.Click += async (_, _) => await updater.CheckAsync(true, cancellation.Token); panel.Children.Add(check);
        var install = Dialogs.Button("更新して再起動", () => { }, true); InstallButton = install; panel.Children.Add(install);
        panel.Children.Add(Dialogs.Note("更新時は再生を終了して再起動します。曲・マイリスト・設定は保持します。"));
        var installing = false;
        void Refresh() { check.IsEnabled = !updater.Busy && !installing; install.IsEnabled = updater.HasUpdate && !updater.Busy && !installing; notes.Text = updater.Available?.Notes ?? "利用可能な更新はありません。"; }
        PropertyChangedEventHandler changed = (_, _) => Refresh(); updater.PropertyChanged += changed;
        install.Click += async (_, _) => {
            if (installing) return; installing = true; Refresh();
            try { var manifest = await updater.PrepareAsync(cancellation.Token); main.ApplyPreparedUpdate(manifest); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsVisible) MessageBox.Show(this, "更新できませんでした。現在のアプリはそのまま使えます。\n" + ex.Message, "Lune"); }
            finally { installing = false; Refresh(); }
        };
        Closed += (_, _) => { updater.PropertyChanged -= changed; cancellation.Cancel(); cancellation.Dispose(); };
        Refresh();
    }
}
