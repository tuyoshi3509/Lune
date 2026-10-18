using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Lune;

public sealed partial class PlayerService
{
    private readonly DispatcherTimer scheduleTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer fadeTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private Playlist? scheduledPlaylist;
    private List<Track> scheduledTracks = new();
    private string scheduleSlot = "";
    private long checkedHour = -1, fadeStarted;
    private Track? fadeTarget;
    private double fadeFrom, fadeSeconds;
    private bool fadingOut, scheduleSwitching;
    internal Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;
    public Func<Playlist, IEnumerable<Track>>? PlaylistTracks { get; set; }
    public string ScheduledPlaylistId => scheduledPlaylist?.Id ?? "";
    public string ScheduleStatus => scheduledPlaylist == null ? "" : $"時間帯再生 · {scheduledPlaylist.Name}";
    internal double ScheduleGain => media.TransitionGain;
    internal bool IsScheduleFading => fadeTimer.IsEnabled;
    internal bool IsScheduleClockRunning => scheduleTimer.IsEnabled;

    private void InitializeSchedule()
    {
        scheduleTimer.Tick += (_, _) => CheckSchedule();
        fadeTimer.Tick += (_, _) => AdvanceScheduleFade();
    }
    private void UpdateScheduleTimer() => scheduleTimer.IsEnabled = IsPlaying && scheduledPlaylist?.ScheduleEnabled == true;
    private void NotifySchedule() { Raise(nameof(ScheduledPlaylistId)); Raise(nameof(ScheduleStatus)); UpdateScheduleTimer(); }
    private List<Track> ScheduledOrder()
    {
        var ids = scheduledPlaylist!.TrackIds.ToHashSet();
        return scheduledTracks.Where(t => ids.Contains(t.Id) && t.ReadIssue.Length == 0).ToList();
    }
    private Track? ScheduledTarget(DateTime now)
    {
        if (scheduledPlaylist == null) return null;
        var range = PlaylistSchedule.At(scheduledPlaylist, now);
        return range == null ? null : scheduledTracks.FirstOrDefault(t => t.Id == range.TrackId && t.ReadIssue.Length == 0);
    }
    private static string Slot(PlaylistTimeRange? range) => range == null ? "" : $"{range.StartHour}/{range.EndHour}/{range.TrackId}";
    private void RememberScheduleTime(DateTime now)
    {
        checkedHour = now.Ticks / TimeSpan.TicksPerHour;
        scheduleSlot = scheduledPlaylist == null ? "" : Slot(PlaylistSchedule.At(scheduledPlaylist, now));
    }
    public void PlayPlaylist(Playlist playlist, IEnumerable<Track> tracks, Track? selected = null)
    {
        var order = tracks.ToList(); var now = LocalNow();
        var range = PlaylistSchedule.At(playlist, now);
        var start = selected ?? order.FirstOrDefault(t => t.Id == range?.TrackId && t.ReadIssue.Length == 0 && File.Exists(t.Path));
        PlayCollection(order, start);
        if (order.Count == 0 || !playlist.ScheduleEnabled || Current == null || !order.Any(t => t.Id == Current.Id)) return;
        scheduledPlaylist = playlist; scheduledTracks = order; RememberScheduleTime(now); NotifySchedule();
    }
    public void RestoreScheduledPlaylist(Playlist playlist, IEnumerable<Track> tracks)
    {
        if (!playlist.ScheduleEnabled || IsQueuePlayback || Current == null || !playlist.TrackIds.Contains(Current.Id)) return;
        scheduledPlaylist = playlist; scheduledTracks = tracks.ToList(); RememberScheduleTime(LocalNow()); NotifySchedule();
    }
    public void RefreshScheduledPlaylist(Playlist? playlist)
    {
        if (scheduledPlaylist == null) return;
        if (playlist == null || !playlist.ScheduleEnabled) { EndScheduledPlayback(); return; }
        scheduledPlaylist = playlist;
        if (PlaylistTracks != null) scheduledTracks = PlaylistTracks(playlist).ToList();
        checkedHour = -1; NotifySchedule(); CheckSchedule();
    }
    private void EndScheduledPlayback()
    {
        CancelScheduleFade(); scheduledPlaylist = null; scheduledTracks.Clear(); checkedHour = -1; scheduleSlot = ""; NotifySchedule();
    }
    private void CancelScheduleFade()
    {
        fadeTimer.Stop(); fadeTarget = null; media.TransitionGain = 1;
    }
    internal void CheckSchedule()
    {
        if (!IsPlaying || scheduledPlaylist?.ScheduleEnabled != true) return;
        var now = LocalNow(); var hour = now.Ticks / TimeSpan.TicksPerHour;
        if (hour == checkedHour) return;
        checkedHour = hour;
        var slot = Slot(PlaylistSchedule.At(scheduledPlaylist, now));
        if (slot == scheduleSlot) return;
        scheduleSlot = slot;
        var target = ScheduledTarget(now);
        if (target == null || target.Id == Current?.Id) { CancelScheduleFade(); return; }
        if (!File.Exists(target.Path)) { Error?.Invoke($"時間帯の曲「{target.Title}」が見つからないため、現在の再生を続けます。"); CancelScheduleFade(); return; }
        fadeTarget = target; fadingOut = true; fadeFrom = media.TransitionGain;
        fadeSeconds = Math.Clamp(double.IsFinite(scheduledPlaylist.ScheduleFadeSeconds) ? scheduledPlaylist.ScheduleFadeSeconds : 5, 1, 30);
        fadeStarted = Stopwatch.GetTimestamp(); fadeTimer.Start();
    }
    private bool ResumeScheduledPlayback()
    {
        if (scheduledPlaylist?.ScheduleEnabled != true) return false;
        var now = LocalNow(); var target = ScheduledTarget(now); RememberScheduleTime(now);
        if (target == null || target.Id == Current?.Id || !File.Exists(target.Path)) return false;
        CancelScheduleFade(); fadeTarget = target;
        fadeSeconds = Math.Clamp(double.IsFinite(scheduledPlaylist.ScheduleFadeSeconds) ? scheduledPlaylist.ScheduleFadeSeconds : 5, 1, 30);
        SwitchScheduledTrack(); return true;
    }
    private void SwitchScheduledTrack()
    {
        var target = fadeTarget;
        if (scheduledPlaylist == null || target == null) { CancelScheduleFade(); return; }
        var order = ScheduledOrder(); var next = order.FindIndex(t => t.Id == target.Id);
        if (next < 0 || !File.Exists(target.Path))
        {
            CancelScheduleFade();
            if (!media.IsPlaying) Stop();
            Error?.Invoke($"時間帯の曲「{target.Title}」を再生できません。マイリストとファイルを確認してください。"); return;
        }
        media.TransitionGain = 0; scheduleSwitching = true;
        try
        {
            if (Current != null) history.Add(Current);
            queue = order; visited.Clear(); priorityNext.Clear(); Load(next, true);
        }
        finally { scheduleSwitching = false; }
        if (!IsPlaying) { CancelScheduleFade(); return; }
        fadingOut = false; fadeTarget = null; fadeFrom = media.TransitionGain;
        fadeStarted = Stopwatch.GetTimestamp(); fadeTimer.Start();
    }
    private void AdvanceScheduleFade()
    {
        if (!IsPlaying || scheduledPlaylist?.ScheduleEnabled != true) { CancelScheduleFade(); return; }
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(fadeStarted).TotalSeconds / fadeSeconds, 0, 1);
        var smooth = progress * progress * (3 - 2 * progress);
        media.TransitionGain = fadingOut ? fadeFrom * (1 - smooth) : fadeFrom + (1 - fadeFrom) * smooth;
        if (progress < 1) return;
        if (fadingOut) SwitchScheduledTrack(); else CancelScheduleFade();
    }
    private bool HandleScheduledEnd()
    {
        if (scheduledPlaylist?.ScheduleEnabled != true || HasWaitingQueue) return false;
        CheckSchedule();
        if (fadeTimer.IsEnabled && fadingOut) { SwitchScheduledTrack(); return true; }
        if (!scheduledPlaylist.ScheduleLoop || ScheduledTarget(LocalNow())?.Id != Current?.Id) return false;
        Seek(0); media.Play(); IsPlaying = media.IsPlaying; return true;
    }
}
