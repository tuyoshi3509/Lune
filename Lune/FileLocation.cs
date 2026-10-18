using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Lune;

internal static class FileLocation
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributes, out uint flags);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
    internal static void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            var folder = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("保存場所が見つかりません。ドライブの接続を確認してください。\n" + folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); return;
        }
        Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, IntPtr.Zero, out var item, 0, out _));
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0)); }
        finally { Marshal.FreeCoTaskMem(item); }
    }
}
