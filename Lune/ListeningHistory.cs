using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Lune;

public sealed class ListeningStats : Observable
{
    private double totalSeconds, trackSeconds;
    public double TotalSeconds
    {
        get => totalSeconds;
        set { if (Set(ref totalSeconds, ValidSeconds(value))) { Raise(nameof(TotalTimeText)); Raise(nameof(TotalSecondsText)); Raise(nameof(EquivalentPlays)); Raise(nameof(PlayCountText)); } }
    }
    public double TrackSeconds
    {
        get => trackSeconds;
        set { if (Set(ref trackSeconds, ValidSeconds(value))) { Raise(nameof(EquivalentPlays)); Raise(nameof(PlayCountText)); Raise(nameof(FormulaText)); } }
    }
    private static double ValidSeconds(double value) => double.IsFinite(value) && value > 0 ? value : 0;
    [JsonIgnore] public double? EquivalentPlays => TrackSeconds > 0 ? TotalSeconds / TrackSeconds : null;
    [JsonIgnore] public string TotalTimeText => $"{Math.Floor(TotalSeconds / 3600):N0}時間 {Math.Floor(TotalSeconds / 60) % 60:00}分 {Math.Floor(TotalSeconds) % 60:00}秒";
    [JsonIgnore] public string TotalSecondsText => $"合計 {TotalSeconds:N1} 秒";
    [JsonIgnore] public string PlayCountText => EquivalentPlays is double count ? $"{count:N2} 回" : "—";
    [JsonIgnore] public string FormulaText => TrackSeconds > 0 ? $"累計再生時間 ÷ 曲の長さ（{PlayerService.FormatTime(TrackSeconds)}）" : "曲の長さを取得できると計算できます。";
}

public sealed class ListeningHistoryData
{
    public Dictionary<string, ListeningStats> Tracks { get; set; } = new();
}

// Separate from metadata so rescans and replacement Track objects retain listening totals.
public sealed class ListeningHistory
{
    private readonly Store store = new();
    private readonly ListeningHistoryData data;
    private readonly Stopwatch checkpoint = Stopwatch.StartNew();
    private bool dirty, saveFailed;
    public event Action<string>? Error;
    public string? Warning => store.Warning;
    public ListeningHistory() => data = store.Load<ListeningHistoryData>("playback-history.json");
    public bool HasListened(Track track) => data.Tracks.TryGetValue(track.Id, out var stats) && stats.TotalSeconds > 0;
    public ListeningStats Get(Track track)
    {
        if (!data.Tracks.TryGetValue(track.Id, out var stats))
            data.Tracks[track.Id] = stats = new ListeningStats { TrackSeconds = track.Seconds };
        return stats;
    }
    public void Add(Track track, double seconds, double decodedDuration)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        var stats = Get(track);
        if (double.IsFinite(decodedDuration) && decodedDuration > 0) stats.TrackSeconds = decodedDuration;
        stats.TotalSeconds += seconds; dirty = true;
    }
    public void SaveIfDue() { if (checkpoint.Elapsed.TotalSeconds >= 15) Save(); }
    public void Save()
    {
        checkpoint.Restart();
        if (!dirty) return;
        try { store.Save("playback-history.json", data); dirty = false; saveFailed = false; }
        catch (Exception ex)
        {
            if (!saveFailed) Error?.Invoke("再生履歴を保存できませんでした。保存先の空き容量とアクセス権を確認してください。 " + ex.Message);
            saveFailed = true;
        }
    }
}
