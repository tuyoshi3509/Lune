using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lune;

/// <summary>One writer per data folder. Further launches signal the existing UI.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? registration;
    public bool IsPrimary { get; }
    public bool ActivationSent { get; }

    public SingleInstance(string dataPath, Action onActivate)
    {
        var id = LibraryService.Id(dataPath);
        mutex = new Mutex(false, "Local\\Lune-" + id);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }

        var eventName = "Local\\Lune-Activate-" + id;
        if (IsPrimary)
        {
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => onActivate(), null, Timeout.Infinite, false);
            return;
        }

        // The first process may still be creating its activation event.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var signal))
            {
                using (signal)
                {
                    GrantForegroundPermission();
                    ActivationSent = signal.Set();
                }
                break;
            }
            Thread.Sleep(50);
        }
    }

    private static void GrantForegroundPermission()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && string.Equals(process.MainModule?.FileName, current.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                        AllowSetForegroundWindow((uint)process.Id);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    public void Dispose()
    {
        registration?.Unregister(null);
        activation?.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
