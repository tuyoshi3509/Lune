using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Lune;
public sealed partial class PlayerService : Observable, IDisposable
{
    private readonly AudioEngine media = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private List<Track> queue = new();
    private readonly List<Track> manualQueue = new();
    private readonly List<Track> history = new();
    private readonly HashSet<int> visited = new();
    private readonly List<Track> priorityNext = new();
    private int index = -1, failed;
    private Track? current;
    private bool playing, opened, pendingPlay, muted, shuffle;
    private double position, duration, volume = 0.15, pendingSeek;
    private double volumeMinimum, volumeMaximum = 0.3;
    private bool changingVolumeRange;
    private int repeat;
    private bool spectrumEnabled;
    private readonly HashSet<object> spectrumViews = new();
    public SpectrumAnalyzer Spectrum => media.Spectrum;
    public bool SpectrumEnabled { get => spectrumEnabled; set { if (Set(ref spectrumEnabled, value)) UpdateSpectrum(); } }
    internal void SetSpectrumVisible(object view, bool visible) { if (visible) spectrumViews.Add(view); else spectrumViews.Remove(view); UpdateSpectrum(); }
    private void UpdateSpectrum() => media.Spectrum.Enabled = SpectrumEnabled && IsPlaying && spectrumViews.Count > 0;
    public ListeningHistory Listening { get; } = new();
    public event Action<string>? Error;
    public event Action? QueueChanged;
    public event Action? TrackChanged;
    public event Action? DevicesChanged;
    public IReadOnlyList<OutputDevice> OutputDevices => media.OutputDevices;
    public IReadOnlyList<OutputDevice> SecondaryOutputDevices => media.SecondaryOutputDevices;
    public string OutputDeviceId { get => media.OutputDeviceId; set => media.SelectDevice(value); }
    public string SecondaryOutputDeviceId { get => media.SecondaryOutputDeviceId; set => media.SelectSecondaryDevice(value); }
    public string ActiveOutputDeviceId => media.ActiveDeviceId;
    public string OutputDeviceLabel => media.OutputDeviceLabel;
    public string SecondaryOutputDeviceLabel => media.SecondaryOutputDeviceLabel;
    public string OutputDeviceNote => media.OutputDeviceNote;
    public string SecondaryOutputDeviceNote => media.SecondaryOutputDeviceNote;
    public bool SecondaryOutputEnabled => SecondaryOutputDeviceId != AudioRoutes.Off;
    public bool PrimaryOutputMuted { get => media.PrimaryMuted; set { media.PrimaryMuted = value; Raise(); Raise(nameof(PrimaryOutputButtonLabel)); Raise(nameof(PrimaryOutputHint)); } }
    public bool SecondaryOutputMuted { get => media.SecondaryMuted; set { media.SecondaryMuted = value; Raise(); Raise(nameof(SecondaryOutputButtonLabel)); Raise(nameof(SecondaryOutputHint)); } }
    public string PrimaryOutputButtonLabel => PrimaryOutputMuted ? "1 · 消音" : "1 · 音声";
    public string SecondaryOutputButtonLabel => !SecondaryOutputEnabled ? "2 · なし" : SecondaryOutputMuted ? "2 · 消音" : "2 · 音声";
    public string PrimaryOutputHint => $"出力1：{OutputDeviceLabel}\nクリックで{(PrimaryOutputMuted ? "ミュート解除" : "ミュート")}\n{OutputDeviceNote}" + (Muted ? "\n全体のミュートがオンです。" : "");
    public string SecondaryOutputHint => !SecondaryOutputEnabled ? "出力2：出力しない（出力設定で変更できます）" : $"出力2：{SecondaryOutputDeviceLabel}\nクリックで{(SecondaryOutputMuted ? "ミュート解除" : "ミュート")}\n{SecondaryOutputDeviceNote}" + (Muted ? "\n全体のミュートがオンです。" : "");
    public void RefreshOutputDevices() => media.RefreshDevices();
    public Track? Current { get => current; private set => Set(ref current, value); }
    public IReadOnlyList<Track> Queue => manualQueue;
    public IReadOnlyList<Track> PlaybackOrder => queue;
    public bool IsQueuePlayback => ReferenceEquals(queue, manualQueue);
    public int QueueIndex => IsQueuePlayback ? index : -1;
    public Func<Track, IEnumerable<Track>>? AlbumTracks { get; set; }
    private bool HasWaitingQueue => manualQueue.Count > (Current != null && manualQueue.Any(t => t.Id == Current.Id) ? 1 : 0);
    public bool IsPlaying { get => playing; private set { if (Set(ref playing, value)) { Raise(nameof(PlayIcon)); Raise(nameof(PlayLabel)); UpdateSpectrum(); UpdateScheduleTimer(); } } }
    public string PlayIcon => IsPlaying ? "\uE769" : "\uE768";
    public string PlayLabel => IsPlaying ? "一時停止 (Space)" : "再生 (Space)";
    public double Position { get => position; private set { if (Set(ref position, value)) Raise(nameof(PositionText)); } }
    public double Duration { get => duration; private set { if (Set(ref duration, value)) Raise(nameof(DurationText)); } }
    public string PositionText => FormatTime(Position);
    public string DurationText => FormatTime(Duration);
    public double Volume
    {
        get => volume;
        set
        {
            if (changingVolumeRange || !double.IsFinite(value)) return;
            Set(ref volume, Math.Clamp(value, VolumeMinimum, VolumeMaximum));
            media.Volume = volume; Raise(nameof(VolumeLabel));
        }
    }
    public double VolumeMinimum => volumeMinimum;
    public double VolumeMaximum => volumeMaximum;
    public double VolumeStep => (VolumeMaximum - VolumeMinimum) / 100;
    public double VolumeLargeStep => VolumeStep * 10;
    public string VolumeRangeLabel => $"音量の範囲：{VolumeMinimum * 100:0.#}〜{VolumeMaximum * 100:0.#}％";
    public string VolumeLabel => $"音量 {Volume * 100:0.#}％（{VolumeMinimum * 100:0.#}〜{VolumeMaximum * 100:0.#}％）";
    public void SetVolumeRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || maximum > 1 || minimum >= maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "0〜100％の範囲で、最小値を最大値より小さくしてください。");
        var targetVolume = Math.Clamp(volume, minimum, maximum);
        volumeMinimum = minimum; volumeMaximum = maximum;
        volume = targetVolume; media.Volume = targetVolume;
        // WPF may coerce a two-way slider's value while its bounds change; publish the pair atomically.
        changingVolumeRange = true;
        try { Raise(nameof(VolumeMinimum)); Raise(nameof(VolumeMaximum)); }
        finally { changingVolumeRange = false; }
        Raise(nameof(Volume)); Raise(nameof(VolumeStep)); Raise(nameof(VolumeLargeStep));
        Raise(nameof(VolumeRangeLabel)); Raise(nameof(VolumeLabel));
    }
    public bool Muted { get => muted; set { Set(ref muted, value); media.IsMuted = value; Raise(nameof(VolumeIcon)); Raise(nameof(PrimaryOutputHint)); Raise(nameof(SecondaryOutputHint)); } }
    public string VolumeIcon => Muted ? "\uE74F" : "\uE767";
    public bool Shuffle { get => shuffle; set { if (Set(ref shuffle, value)) { visited.Clear(); if (index >= 0) visited.Add(index); Raise(nameof(ShuffleLabel)); } } }
    public string ShuffleLabel => Shuffle ? "シャッフル：オン" : "シャッフル：オフ";
    public int Repeat { get => repeat; set { Set(ref repeat, Math.Clamp(value, 0, 2)); Raise(nameof(RepeatLabel)); Raise(nameof(RepeatIcon)); } }
    public string RepeatLabel => Repeat switch { 1 => "リピート：すべて", 2 => "リピート：1曲", _ => "リピート：オフ" };
    public string RepeatIcon => Repeat == 2 ? "\uE8ED" : "\uE8EE";
    public PlayerService()
    {
        InitializeSchedule();
        media.Volume = volume;
        media.Warning += message => Error?.Invoke(message);
        Listening.Error += message => Error?.Invoke(message);
        media.AudioPlayed += seconds => { if (Current != null) Listening.Add(Current, seconds, Duration); };
        media.MediaOpened += (_, _) => {
            opened = true; Duration = media.Duration;
            media.Position = TimeSpan.FromSeconds(Math.Clamp(pendingSeek, 0, Duration)); Position = media.Position.TotalSeconds;
            if (pendingPlay) { media.Play(); IsPlaying = media.IsPlaying; if (IsPlaying) timer.Start(); } else IsPlaying = false;
            failed = 0;
        };
        media.MediaEnded += (_, _) => { if (HandleUtilityEnd()) return; if (HandleScheduledEnd()) return; if (Repeat == 2 && !HasWaitingQueue) { Seek(0); media.Play(); } else Next(true); };
        media.MediaFailed += (_, e) => {
            CancelScheduleFade(); opened = false; timer.Stop(); IsPlaying = false; Listening.Save();
            if (e.ErrorException is AudioDeviceException) { pendingPlay = false; Error?.Invoke(e.ErrorException.Message); return; }
            Error?.Invoke($"「{Current?.Title}」を再生できません。ファイルまたはWindowsの対応コーデックを確認してください。{e.ErrorException.Message}");
            if (pendingPlay && ++failed < Math.Min(queue.Count, 6) && index + 1 < queue.Count) Load(index + 1, true);
        };
        timer.Tick += (_, _) => { if (opened && IsPlaying) Position = media.Position.TotalSeconds; Listening.SaveIfDue(); };
        media.DevicesChanged += () => {
            Position = media.Position.TotalSeconds;
            foreach (var name in new[] { nameof(OutputDeviceId), nameof(OutputDeviceLabel), nameof(ActiveOutputDeviceId), nameof(SecondaryOutputDeviceId), nameof(SecondaryOutputDeviceLabel), nameof(OutputDeviceNote), nameof(SecondaryOutputDeviceNote), nameof(SecondaryOutputEnabled), nameof(SecondaryOutputButtonLabel), nameof(PrimaryOutputHint), nameof(SecondaryOutputHint) }) Raise(name);
            DevicesChanged?.Invoke();
        };
    }
    public static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public void SetQueue(IEnumerable<Track> tracks, Track? start = null, bool play = true, double seek = 0)
        => StartCollection(tracks, start, play, seek, true);
    public void PlayCollection(IEnumerable<Track> tracks, Track? start = null, bool play = true, double seek = 0)
        => StartCollection(tracks, start, play, seek, false);
    private void StartCollection(IEnumerable<Track> tracks, Track? start, bool play, double seek, bool explicitQueue)
    {
        var all = tracks.ToList();
        var list = all.Where(t => t.ReadIssue.Length == 0 || t.Id == start?.Id).ToList();
        if (list.Count < all.Count) Error?.Invoke($"連続再生では読み取り注意の {all.Count - list.Count} 曲をスキップします。曲を指定すると個別に再生を試せます。");
        if (list.Count == 0) return;
        EndScheduledPlayback();
        if (explicitQueue) { manualQueue.Clear(); manualQueue.AddRange(list); queue = manualQueue; }
        else queue = list;
        history.Clear(); visited.Clear(); priorityNext.Clear();
        var n = start == null ? (Shuffle && play ? Random.Shared.Next(queue.Count) : 0) : queue.FindIndex(t => t.Id == start.Id); failed = 0;
        Load(Math.Max(0, n), play, seek); if (explicitQueue) QueueChanged?.Invoke();
    }
    public void Restore(IEnumerable<Track> waiting, IEnumerable<Track> order, Track? currentTrack, bool queuePlayback, double seek)
    {
        manualQueue.Clear(); manualQueue.AddRange(waiting);
        if (currentTrack != null)
        {
            var list = order.ToList();
            if (queuePlayback && manualQueue.Any(t => t.Id == currentTrack.Id)) SetQueue(manualQueue.ToList(), currentTrack, false, seek);
            else PlayCollection(list.Any(t => t.Id == currentTrack.Id) ? list : new[] { currentTrack }, currentTrack, false, seek);
        }
        else if (manualQueue.Count > 0) SetQueue(manualQueue.ToList(), play: false);
        QueueChanged?.Invoke();
    }
    private void Load(int n, bool play, double seek = 0)
    {
        if (!scheduleSwitching) CancelScheduleFade();
        timer.Stop(); media.Close(); Listening.Save(); opened = false; IsPlaying = false;
        ResetTrackUtilities();
        index = n; Current = queue[n]; visited.Add(n); Position = seek; Duration = Current.Seconds;
        pendingSeek = seek; pendingPlay = play;
        TrackChanged?.Invoke();
        if (!File.Exists(Current.Path))
        {
            pendingPlay = false; Error?.Invoke("ファイルが見つかりません。音楽フォルダーまたはドライブの接続を確認してください。\n" + Current.Path); return;
        }
        media.Open(new Uri(Current.Path));
    }
    public void Toggle()
    {
        if (Current == null) return;
        if (!IsPlaying && ResumeScheduledPlayback()) return;
        if (IsPlaying) { media.Pause(); CancelScheduleFade(); timer.Stop(); Position = media.Position.TotalSeconds; IsPlaying = false; pendingPlay = false; Listening.Save(); }
        else if (opened) { pendingPlay = true; media.Play(); IsPlaying = media.IsPlaying; if (IsPlaying) timer.Start(); }
        else Load(index, true, Position);
    }
    public void Stop() { pendingPlay = false; media.Stop(); CancelScheduleFade(); IsPlaying = false; timer.Stop(); Position = 0; pendingSeek = 0; Listening.Save(); }
    public void Seek(double seconds)
    {
        var value = Math.Clamp(seconds, 0, Duration); pendingSeek = value;
        if (opened) media.Position = TimeSpan.FromSeconds(value); Position = value;
    }
    public void Next(bool automatic = false)
    {
        if (!automatic) CancelScheduleFade();
        if (!IsQueuePlayback && manualQueue.Count > 0)
        {
            if (manualQueue[0].Id == Current?.Id) { manualQueue.RemoveAt(0); QueueChanged?.Invoke(); }
            if (manualQueue.Count > 0) { EndScheduledPlayback(); if (Current != null) history.Add(Current); queue = manualQueue; visited.Clear(); priorityNext.Clear(); Load(0, true); return; }
        }
        if (IsQueuePlayback && Current != null)
        {
            var finished = Current;
            if (index >= 0 && index < manualQueue.Count) manualQueue.RemoveAt(index);
            priorityNext.Remove(finished); QueueChanged?.Invoke();
            if (manualQueue.Count > 0)
            {
                history.Add(finished);
                var pending = priorityNext.FirstOrDefault(); priorityNext.RemoveAll(t => ReferenceEquals(t, pending));
                var nextIndex = pending == null ? (Shuffle ? Random.Shared.Next(manualQueue.Count) : Math.Clamp(index, 0, manualQueue.Count - 1)) : manualQueue.IndexOf(pending);
                Load(Math.Max(0, nextIndex), true); return;
            }
            queue = AlbumTracks?.Invoke(finished).Where(t => t.ReadIssue.Length == 0 || t.Id == finished.Id).ToList() ?? new List<Track> { finished };
            index = queue.FindIndex(t => t.Id == finished.Id); visited.Clear(); if (index >= 0) visited.Add(index);
            if (index < 0) { Stop(); return; }
        }
        if (queue.Count == 0) return;
        int next;
        if (priorityNext.Count > 0)
        {
            var t = priorityNext[0]; priorityNext.RemoveAt(0);
            next = queue.FindIndex(index + 1, x => ReferenceEquals(x, t));
            if (next < 0) next = queue.FindIndex(x => ReferenceEquals(x, t));
            if (next < 0) { Next(automatic); return; }
        }
        else if (Shuffle)
        {
            var options = Enumerable.Range(0, queue.Count).Where(i => !visited.Contains(i)).ToList();
            if (options.Count == 0)
            {
                if (automatic && Repeat == 0) { Stop(); return; }
                visited.Clear(); options = Enumerable.Range(0, queue.Count).Where(i => i != index).ToList();
            }
            next = options.Count == 0 ? index : options[Random.Shared.Next(options.Count)];
        }
        else
        {
            next = index + 1;
            if (next >= queue.Count) { if (automatic && Repeat == 0) { Stop(); return; } next = 0; }
        }
        if (Current != null) history.Add(Current); Load(next, true);
    }
    public void Previous()
    {
        CancelScheduleFade();
        if (queue.Count == 0) return;
        if (Position > 3) { Seek(0); return; }
        int n;
        if (history.Count > 0)
        {
            var track = history[^1]; history.RemoveAt(history.Count - 1); n = queue.FindIndex(t => t.Id == track.Id);
            if (n < 0) { queue = AlbumTracks?.Invoke(track).ToList() ?? new List<Track> { track }; n = queue.FindIndex(t => t.Id == track.Id); if (n < 0) { queue = new List<Track> { track }; n = 0; } }
        }
        else n = Math.Max(0, index - 1);
        Load(n, true);
    }
    public void Enqueue(IEnumerable<Track> tracks, bool next)
    {
        var additions = tracks.ToList();
        if (additions.Count == 0) return;
        if (Current == null) { manualQueue.AddRange(additions); SetQueue(manualQueue.ToList(), play: false); return; }
        if (next)
        {
            manualQueue.InsertRange(IsQueuePlayback ? index + 1 : 0, additions);
            if (IsQueuePlayback) { priorityNext.InsertRange(0, additions); visited.Clear(); visited.Add(index); }
        }
        else manualQueue.AddRange(additions);
        QueueChanged?.Invoke();
    }
    public void PlayAt(int n) { if (n >= 0 && n < manualQueue.Count) { EndScheduledPlayback(); if (Current != null) history.Add(Current); queue = manualQueue; visited.Clear(); Load(n, true); } }
    public void RemoveAt(int n)
    {
        if (!IsQueuePlayback) { if (n >= 0 && n < manualQueue.Count) { manualQueue.RemoveAt(n); QueueChanged?.Invoke(); } return; }
        if (n < 0 || n >= queue.Count) return;
        var wasCurrent = n == index; var play = IsPlaying;
        priorityNext.Remove(queue[n]); queue.RemoveAt(n); history.Clear(); visited.Clear();
        if (queue.Count == 0) { Stop(); media.Close(); Current = null; index = -1; Duration = 0; TrackChanged?.Invoke(); }
        else if (wasCurrent) Load(Math.Min(n, queue.Count - 1), play);
        else if (n < index) index--;
        if (index >= 0) visited.Add(index); QueueChanged?.Invoke();
    }
    public void SetLowUpdateRate(bool low) => timer.Interval = TimeSpan.FromMilliseconds(low ? 2000 : 500);
    public void ReleaseCurrentFile()
    {
        CancelScheduleFade();
        pendingPlay = false; timer.Stop(); media.Close(); opened = false; IsPlaying = false; Listening.Save();
    }
    public void RemoveTracks(HashSet<string> ids)
    {
        if (!queue.Any(t => ids.Contains(t.Id)) && !manualQueue.Any(t => ids.Contains(t.Id))) return;
        var removedCurrent = Current != null && ids.Contains(Current.Id);
        if (removedCurrent) { ReleaseCurrentFile(); Current = null; Position = 0; Duration = 0; }
        queue.RemoveAll(t => ids.Contains(t.Id)); priorityNext.RemoveAll(t => ids.Contains(t.Id));
        if (!IsQueuePlayback) manualQueue.RemoveAll(t => ids.Contains(t.Id));
        history.Clear(); visited.Clear(); index = Current == null ? -1 : queue.FindIndex(t => ReferenceEquals(t, Current));
        if (removedCurrent && queue.Count > 0) Load(0, false);
        else { if (index >= 0) visited.Add(index); if (removedCurrent) TrackChanged?.Invoke(); }
        QueueChanged?.Invoke();
    }
    public void SaveListeningHistory() { if (opened) Position = media.Position.TotalSeconds; Listening.Save(); }
    public void Dispose() { DisposeUtilities(); EndScheduledPlayback(); timer.Stop(); media.Dispose(); Listening.Save(); }
}
