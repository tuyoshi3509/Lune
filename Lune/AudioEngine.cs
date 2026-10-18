using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lune;

public sealed class AudioDeviceException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class AudioFailureEventArgs(Exception error) : EventArgs
{
    public Exception ErrorException { get; } = error;
}

// Shared WASAPI sessions affect only Lune; endpoint volumes and system routing are never changed.
public sealed class AudioEngine : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly DeviceNotifications notifications;
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private MediaFoundationReader? reader;
    private readonly List<OutputStream> streams = new();
    private double offset, lastPosition, accountedSeconds, volume = .5;
    private bool muted, playing, disposed;
    private readonly bool[] outputMuted = new bool[2];
    private readonly string[] selectedIds = { "", AudioRoutes.Off };
    private readonly string[] selectedNames = { "", "" };
    private readonly string[] errors = { "", "" };
    private int generation;
    private string defaultId = "";
    private List<OutputDevice> devices = new();
    private readonly Dictionary<string, AudioEndpoint> endpointCache = new();
    private readonly ConcurrentDictionary<string, byte> changedEndpoints = new();
    private int refreshScheduled;
    public SpectrumAnalyzer Spectrum { get; } = new();
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<AudioFailureEventArgs>? MediaFailed;
    public event Action? DevicesChanged;
    public event Action<string>? Warning;
    public event Action<double>? AudioPlayed;
    public string OutputDeviceId => selectedIds[0];
    public string SecondaryOutputDeviceId => selectedIds[1];
    public string ActiveDeviceId => ResolveDeviceId(0);
    public string OutputDeviceLabel => DeviceLabel(0);
    public string SecondaryOutputDeviceLabel => DeviceLabel(1);
    public string OutputDeviceNote => DeviceNote(0);
    public string SecondaryOutputDeviceNote => DeviceNote(1);
    public IReadOnlyList<OutputDevice> OutputDevices => Choices(false);
    public IReadOnlyList<OutputDevice> SecondaryOutputDevices => Choices(true);
    internal int StreamCount => streams.Count;
    internal double[] RenderedPositions => streams.Select(s => s.Position).ToArray();
    internal float[] StreamGains => streams.Select(s => s.Gain.Volume).ToArray();
    private IReadOnlyList<OutputDevice> Choices(bool secondary)
    {
        var list = new List<OutputDevice>();
        if (secondary) list.Add(new(AudioRoutes.Off, "出力しない", "出力設定"));
        list.Add(new("", "Windowsの既定のデバイス"));
        list.AddRange(devices);
        foreach (var index in new[] { 0, 1 })
        {
            var id = selectedIds[index];
            if (id.Length > 0 && id != AudioRoutes.Off && list.All(d => d.Id != id))
                list.Add(new(id, (selectedNames[index].Length > 0 ? selectedNames[index] : "前回選択したデバイス") + "（未接続）", "未接続"));
        }
        return list;
    }
    private string DeviceLabel(int index)
    {
        if (selectedIds[index] == AudioRoutes.Off) return "出力しない";
        var selected = devices.FirstOrDefault(d => d.Id == selectedIds[index]);
        if (selected != null) return selected.Name;
        if (index == 1 && selectedIds[index].Length > 0) return "未接続・出力を停止中";
        var active = devices.FirstOrDefault(d => d.Id == ResolveDeviceId(index));
        return active?.Name ?? "再生デバイスがありません";
    }
    private string DeviceNote(int index)
    {
        if (errors[index].Length > 0) return errors[index];
        var selected = devices.FirstOrDefault(d => d.Id == selectedIds[index]);
        if (selected != null) return selected.Note;
        if (selectedIds[index].Length > 0 && selectedIds[index] != AudioRoutes.Off)
            return index == 0 && !IsCaptureId(selectedIds[index]) ? "未接続のため、Windowsの既定のデバイスを使用しています。" : "未接続のため、この出力を停止しています。";
        return "";
    }
    public double Duration => reader?.TotalTime.TotalSeconds ?? 0;
    public bool IsPlaying => playing;
    public double Volume { get => volume; set { volume = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1); ApplyGain(); } }
    private double transitionGain = 1;
    internal double TransitionGain { get => transitionGain; set { transitionGain = Math.Clamp(double.IsFinite(value) ? value : 1, 0, 1); ApplyGain(); } }
    public bool IsMuted { get => muted; set { muted = value; ApplyGain(); } }
    public bool PrimaryMuted { get => outputMuted[0]; set { outputMuted[0] = value; ApplyGain(); } }
    public bool SecondaryMuted { get => outputMuted[1]; set { outputMuted[1] = value; ApplyGain(); } }
    public TimeSpan Position
    {
        get
        {
            if (playing && streams.Count > 0)
            {
                var rendered = streams.Max(s => s.Position);
                lastPosition = Math.Clamp(Math.Max(lastPosition, offset + rendered), 0, Duration);
                CreditPlayback(rendered);
            }
            return TimeSpan.FromSeconds(lastPosition);
        }
        set
        {
            var resume = playing; ReleaseOutput();
            lastPosition = Math.Clamp(value.TotalSeconds, 0, Duration);
            if (reader != null) reader.CurrentTime = TimeSpan.FromSeconds(lastPosition);
            if (resume) Play();
        }
    }
    public AudioEngine()
    {
        notifications = new DeviceNotifications(QueueDeviceRefresh);
        try { enumerator.RegisterEndpointNotificationCallback(notifications); } catch { }
        deviceTimer.Tick += (_, _) => { deviceTimer.Stop(); Interlocked.Exchange(ref refreshScheduled, 0); RefreshDevices(); };
        RefreshDevices();
    }
    private static bool IsCaptureId(string id) => id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase);
    private string ResolveDeviceId(int index)
    {
        var id = selectedIds[index];
        if (id == AudioRoutes.Off) return "";
        if (id.Length == 0) return defaultId;
        var device = devices.FirstOrDefault(d => d.Id == id);
        if (device != null) return device.CanSelect ? device.TransportId : "";
        // Never redirect a missing second output or a recording route to local speakers.
        return index == 0 && !IsCaptureId(id) ? defaultId : "";
    }
    private void QueueDeviceRefresh(string? changedId = null)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        if (changedId != null) changedEndpoints[changedId] = 0;
        if (Interlocked.Exchange(ref refreshScheduled, 1) != 0) return;
        dispatcher.BeginInvoke(() => { if (!disposed) deviceTimer.Start(); });
    }
    public void RefreshDevices()
    {
        var previous = new[] { ResolveDeviceId(0), ResolveDeviceId(1) };
        foreach (var id in changedEndpoints.Keys) if (changedEndpoints.TryRemove(id, out _)) endpointCache.Remove(id);
        List<AudioEndpoint> Enumerate(DataFlow flow)
        {
            var found = new List<AudioEndpoint>();
            try
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    using (device)
                    {
                        var id = device.ID;
                        if (!endpointCache.TryGetValue(id, out var cached))
                        {
                            // NAudio's named getters repeatedly enumerate every property over COM.
                            // Read the three labels in one pass, then reuse them until this endpoint changes.
                            var properties = device.Properties; var count = properties.Count;
                            string name = "", description = "", driver = "";
                            for (var i = 0; i < count; i++)
                            {
                                var key = properties.Get(i);
                                if (key.Equals(PropertyKeys.PKEY_Device_FriendlyName)) name = properties[i].Value?.ToString() ?? "";
                                else if (key.Equals(PropertyKeys.PKEY_Device_DeviceDesc)) description = properties[i].Value?.ToString() ?? "";
                                else if (key.Equals(PropertyKeys.PKEY_DeviceInterface_FriendlyName)) driver = properties[i].Value?.ToString() ?? "";
                                if (name.Length > 0 && description.Length > 0 && driver.Length > 0) break;
                            }
                            cached = new(id, name.Length > 0 ? name : description.Length > 0 ? description : "名前のないデバイス", description, driver);
                            endpointCache[id] = cached;
                        }
                        found.Add(cached);
                    }
            }
            catch { /* A driver may disappear in the middle of enumeration. */ }
            return found;
        }
        devices = AudioRoutes.Build(Enumerate(DataFlow.Render), Enumerate(DataFlow.Capture));
        var connected = devices.Select(d => d.Id).ToHashSet();
        foreach (var id in endpointCache.Keys.Where(id => !connected.Contains(id)).ToArray()) endpointCache.Remove(id);
        try { using var systemDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = systemDefault.ID; }
        catch { defaultId = ""; }
        if ((previous[0] != ResolveDeviceId(0) || previous[1] != ResolveDeviceId(1)) && reader != null) Rebind();
        DevicesChanged?.Invoke();
    }
    public void SelectDevice(string id) => SelectDevice(0, id);
    public void SelectSecondaryDevice(string id) => SelectDevice(1, id);
    private void SelectDevice(int index, string id)
    {
        id ??= index == 0 ? "" : AudioRoutes.Off;
        if (selectedIds[index] == id) return;
        var device = devices.FirstOrDefault(d => d.Id == id);
        if (device is { CanSelect: false }) { Warning?.Invoke(device.Note); return; }
        selectedIds[index] = id; selectedNames[index] = device?.Name ?? ""; errors[index] = "";
        if (reader != null) Rebind();
        DevicesChanged?.Invoke();
    }
    private void Rebind()
    {
        var resume = playing; var at = Position;
        ReleaseOutput(); lastPosition = at.TotalSeconds;
        if (reader != null) reader.CurrentTime = at;
        if (resume) Play();
    }
    private void ApplyGain()
    {
        foreach (var stream in streams) stream.Gain.Volume = muted || stream.Slots.All(i => outputMuted[i]) ? 0 : (float)(volume * transitionGain);
    }
    public void Open(Uri uri)
    {
        Close();
        try { reader = new MediaFoundationReader(uri.LocalPath); MediaOpened?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Fail(ex); }
    }
    public void Play()
    {
        if (reader == null || playing) return;
        try
        {
            Spectrum.Reset();
            var fanOut = new AudioFanOut(new SpectrumSampleProvider(reader.ToSampleProvider(), Spectrum));
            offset = lastPosition; accountedSeconds = 0;
            var token = ++generation; Array.Fill(errors, "");
            var routes = Enumerable.Range(0, 2).Select(i => (Slot: i, Id: ResolveDeviceId(i))).Where(r => r.Id.Length > 0).GroupBy(r => r.Id);
            foreach (var route in routes)
            {
                MMDevice? endpoint = null; WasapiOut? output = null;
                var slots = route.Select(r => r.Slot).ToArray();
                try
                {
                    endpoint = enumerator.GetDevice(route.Key);
                    output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 120);
                    var gain = new VolumeSampleProvider(fanOut.Subscribe()); output.Init(gain.ToWaveProvider());
                    var stream = new OutputStream(endpoint, output, gain, slots); streams.Add(stream);
                    output.PlaybackStopped += (_, e) => dispatcher.BeginInvoke(() => OnStopped(stream, token, e.Exception));
                }
                catch (Exception ex)
                {
                    try { output?.Dispose(); } finally { endpoint?.Dispose(); }
                    foreach (var slot in slots) errors[slot] = $"出力{slot + 1}を使用できません。別のデバイスを選択してください。（{ex.Message}）";
                }
            }
            if (streams.Count == 0) throw new AudioDeviceException("使用できる出力先がありません。再生デバイスを接続するか、設定で出力先を変更してください。");
            ApplyGain(); playing = true;
            foreach (var stream in streams.ToArray())
            {
                try { stream.Start(); }
                catch (Exception ex) { RemoveFailedStream(stream, ex); }
            }
            if (streams.Count == 0) throw new AudioDeviceException("音声出力を開始できませんでした。別のデバイスを選択してください。");
            foreach (var error in errors.Where(e => e.Length > 0)) Warning?.Invoke(error);
            DevicesChanged?.Invoke();
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void OnStopped(OutputStream stream, int token, Exception? error)
    {
        if (disposed || generation != token || !streams.Contains(stream)) return;
        if (error != null)
        {
            RemoveFailedStream(stream, error);
            RefreshDevices();
            if (generation != token) return;
            if (streams.Count == 0) { Fail(new AudioDeviceException("音声出力が切断されました。接続を確認し、もう一度再生してください。", error)); return; }
            foreach (var slot in stream.Slots) Warning?.Invoke(errors[slot]);
            DevicesChanged?.Invoke(); QueueDeviceRefresh();
        }
        else stream.Ended = true;
        if (streams.Count > 0 && streams.All(s => s.Ended))
        {
            ReleaseOutput(completed: true); lastPosition = Duration; MediaEnded?.Invoke(this, EventArgs.Empty);
        }
    }
    private void RemoveFailedStream(OutputStream stream, Exception error)
    {
        _ = Position; streams.Remove(stream); stream.Dispose();
        foreach (var slot in stream.Slots) errors[slot] = $"出力{slot + 1}が停止しました。他の出力は継続します。（{error.Message}）";
    }
    public void Pause()
    {
        var at = Position; ReleaseOutput(); lastPosition = at.TotalSeconds;
        if (reader != null) reader.CurrentTime = at;
    }
    public void Stop() { ReleaseOutput(); lastPosition = 0; if (reader != null) reader.CurrentTime = TimeSpan.Zero; }
    private void CreditPlayback(double renderedSeconds)
    {
        // One shared timeline: two outputs must not double the listening history.
        var played = Math.Clamp(renderedSeconds, 0, Math.Max(0, Duration - offset));
        var delta = played - accountedSeconds;
        if (delta <= 0) return;
        accountedSeconds = played; AudioPlayed?.Invoke(delta);
    }
    private void ReleaseOutput(bool completed = false)
    {
        if (playing && streams.Count > 0) { if (completed) CreditPlayback(Duration - offset); else _ = Position; }
        generation++; playing = false;
        var old = streams.ToArray(); streams.Clear();
        foreach (var stream in old) stream.Dispose();
        Spectrum.Reset();
    }
    private void Fail(Exception ex) { ReleaseOutput(); MediaFailed?.Invoke(this, new AudioFailureEventArgs(ex)); }
    public void Close() { ReleaseOutput(); reader?.Dispose(); reader = null; lastPosition = 0; }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; deviceTimer.Stop(); Close();
        try { enumerator.UnregisterEndpointNotificationCallback(notifications); } catch { }
        enumerator.Dispose();
    }
    private sealed class OutputStream(MMDevice endpoint, WasapiOut output, VolumeSampleProvider gain, int[] slots) : IDisposable
    {
        public WasapiOut Output => output;
        public VolumeSampleProvider Gain => gain;
        public int[] Slots => slots;
        public bool Ended { get; set; }
        private double position;
        private readonly Stopwatch elapsed = new();
        public void Start() { elapsed.Restart(); output.Play(); }
        public double Position
        {
            get
            {
                try
                {
                    // Some drivers return a stale QPC timestamp before their first audio packet.
                    // NAudio extrapolates it, so never credit more than this stream's elapsed time.
                    var rendered = output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond;
                    position = Math.Max(position, Math.Min(rendered, elapsed.Elapsed.TotalSeconds));
                }
                catch { /* Keep the last clock value while a device disconnects. */ }
                return position;
            }
        }
        public void Dispose() { try { output.Dispose(); } finally { endpoint.Dispose(); } }
    }
    private sealed class DeviceNotifications(Action<string?> changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, DeviceState state) => changed(id);
        public void OnDeviceAdded(string id) => changed(id);
        public void OnDeviceRemoved(string id) => changed(id);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (flow == DataFlow.Render && role == Role.Multimedia) changed(null); }
        public void OnPropertyValueChanged(string id, PropertyKey key)
        {
            if (key.Equals(PropertyKeys.PKEY_Device_FriendlyName) || key.Equals(PropertyKeys.PKEY_Device_DeviceDesc) || key.Equals(PropertyKeys.PKEY_DeviceInterface_FriendlyName)) changed(id);
        }
    }
}
