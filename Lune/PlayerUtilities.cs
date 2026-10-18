using System;
using System.Windows.Threading;

namespace Lune;

public sealed partial class PlayerService
{
    private readonly DispatcherTimer utilityTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTimeOffset? sleepDeadline;
    private double? loopA, loopB;
    private bool stopAfterTrack, utilitiesInitialized;
    public bool StopAfterTrack { get => stopAfterTrack; set { Set(ref stopAfterTrack, value); Raise(nameof(UtilityStatus)); } }
    public string UtilityStatus => (sleepDeadline is { } end ? $"停止予定 {end.LocalDateTime:HH:mm:ss}　" : "")
        + (StopAfterTrack ? "曲の終了で停止　" : "")
        + (loopA is { } a ? $"A {FormatTime(a)} → B {(loopB is { } b ? FormatTime(b) : "未設定")}" : "");
    public void SetSleepTimer(int minutes)
    {
        if (minutes < 0 || minutes > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        sleepDeadline = minutes == 0 ? null : DateTimeOffset.Now.AddMinutes(minutes);
        UpdateUtilityTimer();
    }
    public void SetLoopStart()
    {
        if (Current == null || Duration <= 0) throw new InvalidOperationException("曲を読み込んでから設定してください。");
        EndScheduledPlayback(); loopA = Position; loopB = null; UpdateUtilityTimer();
    }
    public void SetLoopEnd()
    {
        if (loopA == null || Position < loopA + 1) throw new InvalidOperationException("A点より1秒以上後ろでB点を設定してください。");
        loopB = Position; Seek(loopA.Value); UpdateUtilityTimer();
    }
    public void ClearLoop() { loopA = loopB = null; UpdateUtilityTimer(); }
    private void ResetTrackUtilities() { StopAfterTrack = false; ClearLoop(); }
    private bool HandleUtilityEnd()
    {
        if (StopAfterTrack) { StopAfterTrack = false; ClearLoop(); EndScheduledPlayback(); Stop(); return true; }
        if (loopA is { } a && loopB != null) { Seek(a); media.Play(); return true; }
        return false;
    }
    private void UpdateUtilityTimer()
    {
        if (!utilitiesInitialized)
        {
            utilitiesInitialized = true;
            utilityTimer.Tick += (_, _) => {
                if (sleepDeadline is { } end && DateTimeOffset.Now >= end)
                { sleepDeadline = null; StopAfterTrack = false; ClearLoop(); EndScheduledPlayback(); Stop(); }
                else if (IsPlaying && loopA is { } a && loopB is { } b && media.Position.TotalSeconds >= b) Seek(a);
            };
            PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsPlaying)) UpdateUtilityTimer(); };
        }
        utilityTimer.Interval = TimeSpan.FromMilliseconds(IsPlaying && loopB != null ? 100 : 1000);
        utilityTimer.IsEnabled = sleepDeadline != null || (IsPlaying && loopB != null);
        Raise(nameof(UtilityStatus));
    }
    private void DisposeUtilities() => utilityTimer.Stop();
}
