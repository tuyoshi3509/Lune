using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic.FileIO;

namespace Lune;

internal static class LibraryRemoval
{
    internal static void RecycleFile(Track track)
    {
        var path = Path.GetFullPath(track.Path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) != 0) throw new IOException("フォルダーは削除できません。");
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        if (File.Exists(path)) throw new IOException("元ファイルを削除できませんでした。");
    }

    internal static void Unregister(LibraryState library, HashSet<string> ids)
    {
        var paths = library.Tracks.Where(t => ids.Contains(t.Id)).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        library.Tracks.RemoveAll(t => ids.Contains(t.Id));
        library.ExtraFiles.RemoveAll(paths.Contains);
        foreach (var playlist in library.Playlists) playlist.TrackIds.RemoveAll(ids.Contains);
        library.ReadErrors = library.Tracks.Count(t => t.ReadIssue.Length > 0);
    }
}
