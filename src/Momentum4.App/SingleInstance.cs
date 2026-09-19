// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Momentum4.App;

/// <summary>
/// Nur eine Instanz pro Windows-Sitzung (docs/architecture.md §7): ein benannter Mutex, dazu ein benanntes Ereignis, über
/// das ein zweiter Start die erste Instanz bittet, ihr Fenster zu zeigen. Der zweite Start erlaubt der ersten Instanz
/// vorher, sich in den Vordergrund zu holen, und beendet sich dann.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string Name = @"Local\Momentum4Control-3f6b1c2e";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest)
    {
        _mutex = mutex;
        _showRequest = showRequest;
        _registration = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Ein weiterer Start möchte das Fenster sehen (kommt auf einem Threadpool-Thread).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>Erste Instanz: liefert den Halter. Sonst: weckt die erste Instanz und liefert <c>null</c>.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show");
        if (createdNew)
        {
            return new SingleInstance(mutex, showRequest);
        }

        AllowSetForegroundWindow(AsfwAny);
        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _showRequest.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Nicht vom besitzenden Thread freigegeben – Windows gibt den Mutex mit dem Prozess ohnehin frei.
        }

        _mutex.Dispose();
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
