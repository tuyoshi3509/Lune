using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Threading;

namespace Lune;
public partial class App : Application
{
    private SingleInstance? instance;
    public bool ActivationRequested { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var test = e.Args.Contains("--self-test");
        var cropTest = e.Args.Contains("--crop-test");
        var enhancementsTest = e.Args.Contains("--enhancements-test");
        var startupImportTest = e.Args.Contains("--startup-import-test");
        var playlistImportTest = e.Args.Contains("--playlist-import-test");
        var audioOutputTest = e.Args.Contains("--audio-output-test");
        var discordTest = e.Args.Contains("--discord-test");
        var scheduleTest = e.Args.Contains("--schedule-test");
        Paths.Initialize(e.Args);
        if (test && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "unit-data") });
        if (cropTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "crop-data") });
        if (enhancementsTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "enhancements-data") });
        if (startupImportTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", e.Args.Contains("--mini-start") ? "startup-mini-data" : "startup-main-data") });
        if (playlistImportTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "playlist-import-data") });
        if (audioOutputTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "audio-output-data") });
        if (discordTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "discord-data") });
        if (scheduleTest && !e.Args.Contains("--data")) Paths.Initialize(new[] { "--data", Path.Combine(Paths.Root, "test-output", "schedule-data") });
        if (!test)
        {
            instance = new SingleInstance(Paths.Data, () => Dispatcher.BeginInvoke(new Action(() => {
                ActivationRequested = true;
                if (MainWindow is MainWindow main) main.RestoreMain();
            })));
            if (!instance.IsPrimary)
            {
                if (!instance.ActivationSent) MessageBox.Show("起動中のLuneに接続できませんでした。しばらく待ってからもう一度開いてください。", "Lune");
                Shutdown(); return;
            }
        }
        DispatcherUnhandledException += (_, ex) => {
            try { File.AppendAllText(Path.Combine(Paths.Data, "error.log"), DateTime.Now + " " + ex.Exception + Environment.NewLine); } catch { }
            if (test || cropTest || enhancementsTest || startupImportTest || playlistImportTest || audioOutputTest || discordTest || scheduleTest || e.Args.Contains("--distribution-test") || e.Args.Contains("--smoke")) { ex.Handled = true; Shutdown(1); return; }
            MessageBox.Show("操作を完了できませんでした。\n" + ex.Exception.Message, "Lune"); ex.Handled = true;
        };
        if (e.Args.Contains("--setup-tools")) { SetupTools(); return; }
        if (e.Args.Contains("--test-import")) { TestImport(); return; }
        if (startupImportTest) StartupImportChecks.Prepare(e.Args.Contains("--mini-start"));
        var window = new MainWindow(test, cropTest || enhancementsTest || playlistImportTest || audioOutputTest || discordTest || scheduleTest || e.Args.Contains("--smoke"));
        MainWindow = window;
        window.Show();
    }
    private async void SetupTools()
    {
        var log = Path.Combine(Paths.Data, "setup-tools.log");
        try
        {
            await DownloadService.InstallToolsAsync(new Progress<string>(s => File.AppendAllText(log, s + Environment.NewLine)), CancellationToken.None);
            File.AppendAllText(log, "COMPLETE\n"); Shutdown(0);
        }
        catch (Exception ex) { File.AppendAllText(log, ex + "\n"); Shutdown(1); }
    }
    private async void TestImport() { Shutdown(await SelfTests.NetworkImport()); }
    protected override void OnExit(ExitEventArgs e) { DownloadService.StopAll(); instance?.Dispose(); base.OnExit(e); }
}
