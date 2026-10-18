using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Lune;

internal static class UpdateChecks
{
    internal static async Task Run(MainWindow main, bool install)
    {
        var output = Path.Combine(Paths.Data, "update-checks.txt");
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); File.AppendAllText(output, "PASS: " + message + "\n"); }
        try
        {
            Check(AppUpdates.ParseVersion("v1.10.0") > AppUpdates.ParseVersion("v1.9.0"), "numeric release versions are ordered correctly");
            var invalid = false; try { AppUpdates.ParseVersion("v1.2.0-beta"); } catch (InvalidDataException) { invalid = true; }
            Check(invalid, "prerelease version is rejected");
            var folder = Path.Combine(Paths.Data, "archive-check"); Directory.CreateDirectory(folder);
            foreach (var entry in new[] { "Lune/../data/settings.json", "Lune/data/library.json", "Other/Lune.exe" })
            {
                var archive = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("untrusted"); }
                var rejected = false; try { AppUpdates.Extract(archive, folder); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "unapproved ZIP entry rejected: " + entry);
            }
            await main.Updates.CheckAsync(true, main.LifetimeToken);
            Check(!main.Updates.Status.Contains("できません") && !main.Updates.Status.Contains("中断"), "actual public GitHub release check succeeds");
            var cachePath = Path.Combine(Paths.Data, "update-cache.json"); var timestamp = File.GetLastWriteTimeUtc(cachePath);
            var requests = main.Updates.RequestCount; var writes = main.Updates.CacheWrites; var bytes = main.Updates.MetadataBytes;
            await main.Updates.CheckAsync(false, main.LifetimeToken);
            Check(File.GetLastWriteTimeUtc(cachePath) == timestamp, "same-day cached check performs no cache write");
            Check(main.Updates.RequestCount == requests && main.Updates.CacheWrites == writes && main.Updates.MetadataBytes == bytes, "cached redisplay makes zero requests, writes and transfers");
            var first = main.Updates.CheckAsync(true, main.LifetimeToken); var second = main.Updates.CheckAsync(true, main.LifetimeToken);
            Check(ReferenceEquals(first, second), "concurrent checks share one request"); await first;
            Check(main.Updates.RequestCount == requests + 1, "concurrent check issues exactly one HTTP request");
            Check(main.Updates.LastResponseCode == 304 && main.Updates.MetadataBytes == bytes, "unchanged release returns 304 without metadata transfer");
            File.AppendAllText(output, $"Measured: requests={main.Updates.RequestCount}, metadataBytes={main.Updates.MetadataBytes}, cacheWrites={main.Updates.CacheWrites}\n");
            if (install)
            {
                Check(main.Updates.HasUpdate, "newer public release is detected");
                var window = new UpdateWindow(main); window.Show(); window.UpdateLayout();
                Check(window.InstallButton.IsEnabled, "update button is enabled for the newer release");
                SelfTests.Capture(window, "update-available.png");
                window.InstallButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            else main.ExitApp();
        }
        catch (Exception ex) { File.AppendAllText(output, "FAIL: " + ex); Application.Current.Shutdown(1); }
    }
}
