using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lune;

internal static class AudioOutputChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var log = Path.Combine(Paths.Root, "test-output", "audio-output-tests.txt");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        async Task Wait(Func<bool> condition, string name)
        {
            var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 8) await Task.Delay(80);
            Check(condition(), name);
        }
        async Task MeasureCpu(string label)
        {
            await Task.Delay(1000); // Let first-paint/JIT and device notifications settle before sampling.
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew(); await Task.Delay(2000); process.Refresh();
            var used = process.TotalProcessorTime - cpu;
            results.Add($"{label}: CPU={used.TotalMilliseconds:0.0}ms / {clock.Elapsed.TotalSeconds:0.0}s ({used.TotalSeconds / clock.Elapsed.TotalSeconds * 100:0.00}% of one core); RAM={process.WorkingSet64 / 1048576d:0.0} MiB (test process)");
        }
        try
        {
            main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Preferences.Folders.Clear();
            Check(new Settings().SecondaryOutputDeviceId == AudioRoutes.Off, "new and migrated settings disable output 2 by default");
            var old = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"OutputDeviceId\":\"saved-primary\"}")!;
            Check(old.SecondaryOutputDeviceId == AudioRoutes.Off && old.OutputDeviceId == "saved-primary", "old settings preserve output 1 and leave output 2 disabled");
            CheckFanOut(Check);
            CheckRoutes(Check);
            using var engine = new AudioEngine { IsMuted = true, Volume = .19 };
            Exception? failure = null; engine.MediaFailed += (_, e) => { failure = e.ErrorException; results.Add("AUDIO FAILURE: " + e.ErrorException); };
            engine.Warning += message => results.Add("AUDIO WARNING: " + message);
            var render = engine.OutputDevices.Where(d => d.Group == "再生デバイス" && d.Id.Length > 0).ToList();
            var recording = engine.OutputDevices.Where(d => d.IsRecording).ToList();
            foreach (var device in engine.OutputDevices) results.Add($"DEVICE: {device.Group} | {device.Name} | selectable={device.CanSelect} | {device.RenderId}");
            Check(render.Count >= 2, "two real Windows playback endpoints available");
            Check(recording.Count > 0, "recording devices follow playback devices");
            Check(engine.OutputDevices.ToList().FindIndex(d => d.IsRecording) > render.Count, "recording section is below every playback device");
            Check(recording.Any(d => !d.CanSelect) && recording.Any(d => d.CanSelect), "physical microphones disabled and installed virtual routes enabled");
            Check(recording.Where(d => d.CanSelect).All(d => render.Any(r => r.Id == d.RenderId)), "each selectable recording route has a real playback transport");
            var path = Path.Combine(Paths.Data, "silent-dual.wav");
            // Silence is intentional: exercise real devices without broadcasting tones to other apps.
            using (var wave = new WaveFileWriter(path, new WaveFormat(44100, 16, 2))) wave.Write(new byte[44100 * 4 * 30], 0, 44100 * 4 * 30);
            engine.SelectDevice(render[0].Id); engine.Open(new Uri(path)); engine.Play();
            results.Add($"START: {render[0].Name}; playing={engine.IsPlaying}; streams={engine.StreamCount}; duration={engine.Duration}");
            await Wait(() => engine.Position.TotalSeconds > .3, "single output advances WASAPI clock");
            await MeasureCpu("Single output");
            Check(engine.StreamCount == 1 && engine.SecondaryOutputDeviceId == AudioRoutes.Off, "disabled second output opens no stream");
            engine.SelectSecondaryDevice(render[1].Id);
            await Wait(() => engine.RenderedPositions.Length == 2 && engine.RenderedPositions.All(p => p > .3), "both real WASAPI output clocks advance concurrently");
            results.Add("Dual device clock difference: " + Math.Abs(engine.RenderedPositions[0] - engine.RenderedPositions[1]).ToString("0.000") + " seconds");
            await MeasureCpu("Dual output");
            Check(engine.IsPlaying && engine.StreamGains.All(g => g == 0), "master mute applies to both streams");
            engine.IsMuted = false; engine.PrimaryMuted = true;
            Check(engine.StreamGains.SequenceEqual(new[] { 0f, .19f }), "mute output 1 without muting output 2");
            engine.PrimaryMuted = false; engine.SecondaryMuted = true;
            Check(engine.StreamGains.SequenceEqual(new[] { .19f, 0f }), "mute output 2 without muting output 1");
            engine.SecondaryMuted = false;
            engine.Volume = .07; Check(engine.StreamGains.All(g => Math.Abs(g - .07) < .00001), "shared volume applies independently to both gains");
            engine.IsMuted = true; engine.Pause();
            var paused = engine.Position.TotalSeconds; await Task.Delay(180);
            Check(!engine.IsPlaying && engine.StreamCount == 0 && Math.Abs(engine.Position.TotalSeconds - paused) < .01, "pause releases both streams and freezes position");
            engine.Position = TimeSpan.FromSeconds(5); engine.Play();
            Check(engine.Position.TotalSeconds < 5.25, "starting device clocks cannot jump the seek position forward");
            await Wait(() => engine.Position.TotalSeconds > 5.3 && engine.RenderedPositions.All(p => p > .2), "seek resumes both outputs from common position");
            engine.Stop(); double credited = 0; engine.AudioPlayed += s => credited += s; engine.Play();
            await Wait(() => engine.Position.TotalSeconds > 1.1, "dual output listening clock progresses"); engine.Pause();
            Check(credited > .8 && credited < 1.8, "two outputs credit listening time once");
            var beforeSeek = credited; engine.Position = TimeSpan.FromSeconds(10); Check(credited == beforeSeek, "seek does not credit skipped seconds");
            engine.Play(); await Wait(() => engine.Position.TotalSeconds > 10.3, "dual output resumes after listening check");
            engine.SelectSecondaryDevice("missing-secondary-for-test");
            results.Add($"DISCONNECTED: playing={engine.IsPlaying}; streams={engine.StreamCount}; selection={engine.SecondaryOutputDeviceId}; position={engine.Position}");
            Check(engine.IsPlaying && engine.StreamCount == 1 && engine.SecondaryOutputDeviceId == "missing-secondary-for-test", "disconnected secondary does not fall back or stop primary");
            engine.SelectSecondaryDevice(render[0].Id);
            Check(engine.StreamCount == 1, "same endpoint selected twice is rendered once without doubling volume");
            engine.IsMuted = false; engine.PrimaryMuted = true; engine.SecondaryMuted = false;
            Check(engine.StreamGains.Single() > 0, "same endpoint stays audible when either logical output is unmuted");
            engine.SecondaryMuted = true; Check(engine.StreamGains.Single() == 0, "same endpoint silences when both logical outputs are muted");
            engine.IsMuted = true;
            var virtualRoute = recording.First(d => d.CanSelect);
            engine.SelectSecondaryDevice(virtualRoute.Id);
            Check(engine.SecondaryOutputDeviceId == virtualRoute.Id && engine.SecondaryOutputDeviceNote.Contains("Voicemeeter"), "recording choice retains capture identity and shows routing instructions");
            await Wait(() => engine.RenderedPositions.All(p => p > .1), "virtual recording route opens its WASAPI playback transport");
            var physical = recording.First(d => !d.CanSelect); engine.SelectSecondaryDevice(physical.Id);
            Check(engine.SecondaryOutputDeviceId == virtualRoute.Id, "unsupported physical microphone cannot be selected programmatically");
            engine.SelectSecondaryDevice(render[1].Id); engine.Position = TimeSpan.FromSeconds(29.5); var ended = 0; engine.MediaEnded += (_, _) => ended++;
            await Wait(() => ended == 1, "both streams complete with one track-end notification"); await Task.Delay(180);
            Check(ended == 1 && engine.StreamCount == 0 && failure == null, "completed streams are disposed and no audio failure occurred");

            main.P.Muted = true; main.P.SecondaryOutputDeviceId = AudioRoutes.Off;
            main.P.PrimaryOutputMuted = false; main.P.SecondaryOutputMuted = false;
            var settings = new SettingsWindow(main); settings.Show(); await Task.Delay(100);
            settings.DeviceSelector.SelectedValue = render[0].Id; settings.SecondaryDeviceSelector.SelectedValue = render[1].Id;
            Check(main.P.OutputDeviceId == render[0].Id && main.P.SecondaryOutputDeviceId == render[1].Id, "both settings selectors control corresponding routes");
            var groups = ((ListCollectionView)settings.SecondaryDeviceSelector.ItemsSource).Groups!;
            Check(((CollectionViewGroup)groups[0]).Name.ToString() == "出力設定" && groups.Cast<CollectionViewGroup>().Any(g => g.Name.ToString() == "録音デバイス"), "dropdown groups include disabled default and recording section");
            main.PrimaryMuteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(main.P.PrimaryOutputMuted && !main.P.SecondaryOutputMuted, "main player output 1 mute takes one click");
            main.SecondaryMuteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(main.P.SecondaryOutputMuted, "main player output 2 mute takes one click");
            main.SaveSettings(); var saved = new Store().Load<Settings>("settings.json");
            Check(saved.PrimaryOutputMuted && saved.SecondaryOutputMuted && saved.SecondaryOutputDeviceId == render[1].Id, "both mute states and output 2 persist");
            settings.SecondaryDeviceSelector.SelectedValue = virtualRoute.Id;
            settings.SecondaryDeviceSelector.BringIntoView(); await Task.Delay(120);
            SelfTests.Capture(settings, "lune-dual-output-settings.png");
            settings.SecondaryDeviceSelector.IsDropDownOpen = true; await Task.Delay(120); settings.UpdateLayout();
            var popup = (Popup)settings.SecondaryDeviceSelector.Template.FindName("PART_Popup", settings.SecondaryDeviceSelector);
            var selectedItem = (ComboBoxItem)settings.SecondaryDeviceSelector.ItemContainerGenerator.ContainerFromItem(virtualRoute);
            selectedItem?.BringIntoView(); await Task.Delay(120);
            Check(popup.IsOpen && popup.Child is FrameworkElement { ActualHeight: > 100 }, "recording section dropdown opens with readable grouped contents");
            SelfTests.Capture((FrameworkElement)popup.Child, "lune-recording-devices.png");
            settings.SecondaryDeviceSelector.IsDropDownOpen = false; settings.Close();
            main.P.SecondaryOutputDeviceId = render[1].Id; main.UpdateLayout();
            SelfTests.Capture(main, "lune-dual-output-main.png");
            main.Width = 980; main.UpdateLayout();
            Check(main.SecondaryMuteButton.TranslatePoint(new Point(main.SecondaryMuteButton.ActualWidth, 0), main).X < main.ActualWidth, "output controls remain inside smallest supported main window");
            SelfTests.Capture(main, "lune-dual-output-main-small.png");
            main.ShowMini(); await Task.Delay(120);
            var mini = Application.Current.Windows.OfType<MiniWindow>().Single();
            mini.PrimaryMuteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); mini.SecondaryMuteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!main.P.PrimaryOutputMuted && !main.P.SecondaryOutputMuted, "mini output buttons independently unmute corresponding routes");
            Check(mini.PrimaryMuteButton.ActualWidth >= 57 && mini.SecondaryMuteButton.ActualWidth >= 57, "mini mute buttons keep their full hit targets");
            SelfTests.Capture(mini, "lune-dual-output-mini.png"); main.RestoreMain();
            main.P.SecondaryOutputDeviceId = AudioRoutes.Off; main.UpdateLayout();
            Check(!main.SecondaryMuteButton.IsEnabled && main.P.SecondaryOutputButtonLabel.Contains("なし"), "disabled output 2 shows no-output state");
            results.Add("PASS: all audio output checks complete"); File.WriteAllLines(log, results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(log, results); Application.Current.Shutdown(1); }
    }
    private static void CheckFanOut(Action<bool, string> check)
    {
        var source = new SequenceSource(48000 * 2 * 8); var hub = new AudioFanOut(source);
        var a = hub.Subscribe(); var b = hub.Subscribe(); var first = new float[1920]; var second = new float[1920];
        for (var i = 0; i < 110; i++) { a.Read(first, 0, first.Length); b.Read(second, 0, 640); b.Read(second, 640, 1280); if (!first.SequenceEqual(second)) throw new InvalidOperationException("Fan-out sample mismatch"); }
        check(source.ReadCount == 1920 * 110, "fan-out decodes once and both outputs receive identical samples across ring wrap");
        check(hub.Capacity == 48000 * 2 * 2, "fan-out memory bounded to two seconds of PCM");
        for (var i = 0; i < 120; i++) a.Read(first, 0, first.Length);
        check(b.Read(second, 0, second.Length) > 0, "stalled output catches up without blocking other consumer");
        while (a.Read(first, 0, first.Length) > 0) { }
        while (b.Read(second, 0, second.Length) > 0) { }
        check(source.ReadCount == 48000 * 2 * 8 && a.Read(first, 0, 2) == 0 && b.Read(second, 0, 2) == 0, "both consumers terminate exactly once source ends");
        var bytes = new byte[400]; var waveProvider = new AudioFanOut(new SequenceSource(1000)).Subscribe().ToWaveProvider();
        check(waveProvider.Read(bytes, 0, bytes.Length) == 400 && Math.Abs(BitConverter.ToSingle(bytes, 396) - 99 / 4096f) < .000001, "fan-out supports NAudio byte-backed float buffers without type mismatch");
        var gainSource = new SequenceSource(1000); var gain = new VolumeSampleProvider(gainSource) { Volume = 0 };
        gain.Read(first, 0, 100); check(first.Take(100).All(x => x == 0), "output gain produces silent PCM when muted");
    }
    private static void CheckRoutes(Action<bool, string> check)
    {
        var routes = AudioRoutes.Build(new[] { new AudioEndpoint("cable-in", "Renamed cable", "CABLE Input", "VB-Audio Virtual Cable"), new AudioEndpoint("vm-in", "Mixer", "Voicemeeter AUX Input", "VB-Audio Voicemeeter VAIO") },
            new[] { new AudioEndpoint("cable-out", "Recorder", "CABLE Output", "VB-Audio Virtual Cable"), new AudioEndpoint("vm-out", "Voice input", "Voicemeeter Out B2", "VB-Audio Voicemeeter VAIO"), new AudioEndpoint("mic", "CABLE Output", "Microphone", "Physical microphone") });
        check(routes.Single(d => d.Id == "cable-out").RenderId == "cable-in", "virtual cable mapping survives renamed friendly names");
        check(routes.Single(d => d.Id == "vm-out").Note.Contains("B2") && routes.Single(d => d.Id == "vm-out").RenderId == "vm-in", "Voicemeeter B2 shows explicit external bus routing requirement");
        check(!routes.Single(d => d.Id == "mic").CanSelect, "physical microphone named like cable is not misidentified as virtual");
        check(!AudioRoutes.Build(Array.Empty<AudioEndpoint>(), new[] { new AudioEndpoint("c", "c", "CABLE Output", "VB-Audio") }).Single().CanSelect, "capture endpoint without playback transport is disabled");
    }
    private sealed class SequenceSource(int count) : ISampleProvider
    {
        public int ReadCount { get; private set; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int requested)
        {
            var take = Math.Min(requested, count - ReadCount);
            for (var i = 0; i < take; i++) buffer[offset + i] = ((ReadCount + i) % 4096) / 4096f;
            ReadCount += take; return take;
        }
    }
}
