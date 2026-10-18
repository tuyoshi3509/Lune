using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Lune;

public sealed record SortOption(string Id, string Name);
public static class LibraryOrdering
{
    public static List<Track> Tracks(IEnumerable<Track> tracks, string mode) => mode switch
    {
        "nameAsc" => tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Artist).ThenBy(t => t.Id).ToList(),
        "nameDesc" => tracks.OrderByDescending(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Artist).ThenBy(t => t.Id).ToList(),
        "addedAsc" => tracks.OrderBy(t => t.AddedUtcTicks).ThenBy(t => t.Title).ThenBy(t => t.Id).ToList(),
        "addedDesc" => tracks.OrderByDescending(t => t.AddedUtcTicks).ThenBy(t => t.Title).ThenBy(t => t.Id).ToList(),
        _ => tracks.ToList()
    };
    public static List<AlbumCard> Albums(IEnumerable<AlbumCard> cards, string mode) => mode switch
    {
        "nameDesc" => cards.OrderByDescending(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Subtitle).ToList(),
        "addedAsc" => cards.OrderBy(c => c.AddedUtcTicks).ThenBy(c => c.Title).ThenBy(c => c.Subtitle).ToList(),
        "addedDesc" => cards.OrderByDescending(c => c.AddedUtcTicks).ThenBy(c => c.Title).ThenBy(c => c.Subtitle).ToList(),
        _ => cards.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Subtitle).ToList()
    };
    public static Task<Dictionary<string, long>> EstimateExistingDatesAsync(IEnumerable<Track> tracks)
    {
        var pending = tracks.Where(t => t.AddedUtcTicks <= 0).ToList();
        return Task.Run(() => pending.ToDictionary(t => t.Id, t =>
        {
            try { var date = File.GetCreationTimeUtc(t.Path); if (date.Year > 1601 && date <= DateTime.UtcNow.AddDays(1)) return date.Ticks; } catch { }
            return t.Modified > 0 ? t.Modified : DateTime.UtcNow.Ticks;
        }));
    }
}
