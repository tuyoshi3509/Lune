using System;
using System.Collections.Generic;
using System.Linq;

namespace Lune;

public sealed class PlaylistTimeRange
{
    public int StartHour { get; set; } = 8;
    public int EndHour { get; set; } = 12;
    public string TrackId { get; set; } = "";
    public bool Contains(int hour) => StartHour is >= 0 and < 24 && EndHour is >= 0 and <= 24 && StartHour != EndHour &&
        (StartHour < EndHour ? hour >= StartHour && hour < EndHour : hour >= StartHour || hour < EndHour);
    public PlaylistTimeRange Copy() => new() { StartHour = StartHour, EndHour = EndHour, TrackId = TrackId };
}

internal static class PlaylistSchedule
{
    internal static PlaylistTimeRange? At(Playlist playlist, DateTime localTime) =>
        playlist.ScheduleEnabled ? playlist.TimeRanges.FirstOrDefault(r => r.Contains(localTime.Hour) && playlist.TrackIds.Contains(r.TrackId)) : null;

    internal static string? Validate(IEnumerable<PlaylistTimeRange> ranges, IEnumerable<string> trackIds)
    {
        var ids = trackIds.ToHashSet(); var hours = new bool[24]; var index = 0;
        foreach (var range in ranges)
        {
            index++;
            if (range.StartHour is < 0 or > 23 || range.EndHour is < 0 or > 24 || range.StartHour == range.EndHour)
                return $"{index}行目：開始・終了を別の時刻にしてください。終日は0時〜24時で指定します。";
            if (!ids.Contains(range.TrackId)) return $"{index}行目：マイリスト内の曲を選択してください。";
            for (var hour = 0; hour < 24; hour++)
            {
                if (!range.Contains(hour)) continue;
                if (hours[hour]) return $"{hour:00}時台の時間帯が重なっています。範囲を調整してください。";
                hours[hour] = true;
            }
        }
        return null;
    }
}
